namespace Elarion.Abstractions;

/// <summary>How a client presents a file response: saved as a download, or shown in place.</summary>
public enum FileDisposition {
    /// <summary><c>Content-Disposition: attachment</c>: the browser saves the file.</summary>
    Attachment,

    /// <summary>
    /// <c>Content-Disposition: inline</c>: the browser shows the file in place (a PDF or an image in a tab), still
    /// with its file name for the tab title and a later save. Reserve it for content types the browser renders safely;
    /// a user-supplied type such as <c>text/html</c> or <c>image/svg+xml</c> served inline from the application's own
    /// origin runs as part of it.
    /// </summary>
    Inline
}

/// <summary>
/// A streamed file response of an HTTP-only <c>[HttpEndpoint]</c> handler: content that is written to the response
/// body after the handler pipeline has run, so it is never buffered — a stored blob, or an archive built on the fly.
/// It keeps what a host-mapped route loses: authorization, the module's feature gate, validation and the declared
/// error contract all apply before a byte is written.
/// </summary>
/// <remarks>
/// <para>
/// Only HTTP can carry it. The JSON surfaces (JSON-RPC, MCP, connections) and the replaying decorators
/// (<c>[Idempotent]</c>, <c>[Cacheable]</c>) need a payload that serializes, so a <c>[Handler]</c> or a cached or
/// idempotent handler that responds with it is a compile-time error. A file a JSON client must receive is an
/// <see cref="ElarionFile"/> (small) or a pending blob the client downloads (large); see ADR-0098.
/// </para>
/// <para>
/// The content is produced after the pipeline has finished: in the request's service scope, but after the handler's
/// transaction has committed. Open what the content needs (a blob read, the rows of an archive) in the handler, where
/// a missing record can still answer <c>404</c>, and write the bytes in the content. Once the first byte is sent a
/// failure can no longer change the status, so the HTTP layer aborts the connection instead of completing a truncated
/// file.
/// </para>
/// <para>
/// When only reading the content can tell whether the file exists, or what its name and type are — an attachment found
/// while parsing a stored message — return a <see cref="Deferred"/> file: its open runs after the pipeline too, and
/// answers the file to serve or an <see cref="AppError"/>, which is sent as an ordinary failure because no byte has been
/// written yet.
/// </para>
/// <para>
/// The instance owns its content: the HTTP layer disposes it with the response. A caller that invokes the handler
/// in process (a test, a module API) disposes it, and opens a deferred file with <see cref="OpenAsync"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // A stored blob, streamed to the caller with its name, shown in the browser when asked to:
/// var download = await blobs.OpenReadAsync(blobRef, ct);
/// if (download is null) return AppError.NotFound("No such document.");
/// return ElarionStreamedFile.FromStream(download.Content, download.Metadata.ContentType, owner: download) with {
///     FileName = document.FileName,
///     Disposition = query.Inline ? FileDisposition.Inline : FileDisposition.Attachment,
///     EntityTag = download.Metadata.ContentSha256,
///     CacheControl = "private, max-age=3600",
/// };
///
/// // An archive built while it is sent:
/// return ElarionStreamedFile.FromWriter(async (body, ct) => {
///     await using var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, null, ct);
///     foreach (var entry in entries) { … }
/// }, "application/zip") with { FileName = "documents.zip" };
///
/// // An attachment found only while parsing the stored message: 404 before the first byte when it is not there.
/// return ElarionStreamedFile.Deferred(async ct => {
///     var download = await blobs.OpenReadAsync(message.BlobRef, ct);
///     var part = download is null ? null : await MimeAttachments.FindAsync(download.Content, query.Part, ct);
///     if (part is null) {
///         if (download is not null) await download.DisposeAsync();
///         return AppError.NotFound("No such attachment.");
///     }
///     // The part decodes forward-only, but its size is known: state it, so the browser shows progress.
///     return ElarionStreamedFile.FromStream(part.Content, part.ContentType, owner: download) with {
///         FileName = part.FileName,
///         Length = part.SizeBytes,
///     };
/// });
/// </code>
/// </example>
public sealed record ElarionStreamedFile : IAsyncDisposable {
    private readonly Stream? _content;
    private readonly IAsyncDisposable? _owner;
    private readonly Func<Stream, CancellationToken, Task>? _writer;
    private readonly Func<CancellationToken, ValueTask<Result<ElarionStreamedFile>>>? _open;
    private int _disposed;

    private ElarionStreamedFile(
        string contentType,
        Stream? content,
        IAsyncDisposable? owner,
        Func<Stream, CancellationToken, Task>? writer,
        Func<CancellationToken, ValueTask<Result<ElarionStreamedFile>>>? open = null) {
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        ContentType = contentType;
        _content = content;
        _owner = owner;
        _writer = writer;
        _open = open;
    }

    /// <summary>
    /// A file whose bytes are <paramref name="content"/>, copied to the response from its current position. The
    /// content stream, and <paramref name="owner"/> after it, are disposed with the response.
    /// </summary>
    /// <param name="content">
    /// The content; a seekable stream also lets the response state its length. For a forward-only stream whose size is
    /// known, state it with <c>with { Length = … }</c>.
    /// </param>
    /// <param name="contentType">The MIME content type.</param>
    /// <param name="owner">
    /// What else lives as long as <paramref name="content"/> is read, such as the <c>BlobDownload</c> whose
    /// <c>Content</c> it is (and the database reader behind it).
    /// </param>
    public static ElarionStreamedFile FromStream(Stream content, string contentType, IAsyncDisposable? owner = null) {
        ArgumentNullException.ThrowIfNull(content);
        return new ElarionStreamedFile(contentType, content, owner, null);
    }

    /// <summary>
    /// A file whose bytes <paramref name="writeAsync"/> writes to the response body; the server owns the body, so
    /// disposing it (a writer's <c>leaveOpen: false</c>) does nothing. The body tolerates the small synchronous writes some writers make while disposing (an entry of
    /// <c>System.IO.Compression.ZipArchive</c> flushes its trailer synchronously even through the async API): they
    /// are held in memory and sent with the next asynchronous write, up to 1 MiB at a time. Write the bulk of the
    /// content asynchronously.
    /// </summary>
    /// <param name="writeAsync">Writes the content; its token is cancelled when the client goes away.</param>
    /// <param name="contentType">The MIME content type.</param>
    public static ElarionStreamedFile FromWriter(Func<Stream, CancellationToken, Task> writeAsync, string contentType) {
        ArgumentNullException.ThrowIfNull(writeAsync);
        return new ElarionStreamedFile(contentType, null, null, writeAsync);
    }

    /// <summary>
    /// A file decided after the pipeline: <paramref name="open"/> runs where the content would (in the request's scope,
    /// after the handler's transaction) and answers either the file to serve — <see cref="FromStream"/> or
    /// <see cref="FromWriter"/>, with its own name, type and caching headers — or an <see cref="AppError"/>, sent as the
    /// ordinary failure response because nothing has been written yet. One read of the content both decides and serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything a response states comes from the file <paramref name="open"/> answers; the deferred file itself
    /// carries none of it, so <c>with { FileName = … }</c> on it has no effect. Decide what the pipeline can decide —
    /// authorization, whether the record exists — in the handler, and defer only what needs the content.
    /// </para>
    /// <para>
    /// The HTTP layer disposes the answered file with the response. When <paramref name="open"/> answers an error, it
    /// disposes what it opened itself. An exception it throws is an ordinary unexpected error, since no byte is sent.
    /// </para>
    /// </remarks>
    /// <param name="open">Decides the file; its token is cancelled when the client goes away.</param>
    public static ElarionStreamedFile Deferred(Func<CancellationToken, ValueTask<Result<ElarionStreamedFile>>> open) {
        ArgumentNullException.ThrowIfNull(open);
        return new ElarionStreamedFile("application/octet-stream", null, null, null, open);
    }

    /// <summary>Whether this is a <see cref="Deferred"/> file, which <see cref="OpenAsync"/> turns into the file to serve.</summary>
    public bool IsDeferred => _open is not null;

    /// <summary>
    /// The file to serve: for a <see cref="Deferred"/> file, what its open answers (the file, or the refusal); for any
    /// other file, the file itself.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="InvalidOperationException">A deferred file's open answered another deferred file.</exception>
    public async ValueTask<Result<ElarionStreamedFile>> OpenAsync(CancellationToken cancellationToken) {
        if (_open is null) return this;

        var opened = await _open(cancellationToken).ConfigureAwait(false);
        if (opened.IsSuccess && opened.Value.IsDeferred) {
            await opened.Value.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("A deferred file's open answered another deferred file; answer the file.");
        }

        return opened;
    }

    /// <summary>The MIME content type. A <see cref="Deferred"/> file has none of its own; the opened file states it.</summary>
    public string ContentType { get; }

    /// <summary>
    /// The file name the client saves or titles the file with, sent in <c>Content-Disposition</c> (UTF-8 encoded, with
    /// an ASCII fallback). <see langword="null"/> sends the disposition without a name.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Whether the client saves the file or shows it in place. Defaults to <see cref="FileDisposition.Attachment"/>:
    /// a streamed file always states a disposition, so the browser never decides by itself to render it.
    /// </summary>
    public FileDisposition Disposition { get; init; } = FileDisposition.Attachment;

    /// <summary>
    /// The entity tag of this content, without quotes; sent as a strong <c>ETag</c>, and a request whose
    /// <c>If-None-Match</c> carries it answers <c>304 Not Modified</c> without writing the content. Use a value that
    /// changes exactly when the bytes do, such as a content hash or a version.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty, or holds a character an entity tag cannot carry (a quote,
    /// a space, a control or non-ASCII character).</exception>
    public string? EntityTag {
        get;
        init {
            if (value is not null && !IsOpaqueTag(value))
                throw new ArgumentException(
                    "An entity tag is a non-empty value of visible ASCII characters other than '\"'.", nameof(value));

            field = value;
        }
    }

    /// <summary>When the content last changed; sent as <c>Last-Modified</c> and honoured for <c>If-Modified-Since</c>.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// The <c>Cache-Control</c> value, such as <c>private, max-age=3600</c>; <see langword="null"/> sends none. Keep
    /// files behind authorization <c>private</c>, so no shared cache hands them to another user.
    /// </summary>
    public string? CacheControl { get; init; }

    /// <summary>
    /// The number of bytes the content holds, when it is known before it is written but the content cannot state it
    /// itself: a forward-only stream whose size the reader knows (a decoded message part), or a writer that produces a
    /// known count. Sent as <c>Content-Length</c>, so a client shows the download's progress, and checked against the
    /// bytes actually written: a content that writes more or fewer bytes fails, which aborts the connection like any
    /// other failure after the first byte. <see langword="null"/> (the default) leaves the length to the content: a
    /// seekable stream states its own, anything else is sent chunked. On a seekable stream it must equal the bytes
    /// from the current position to the end. A <see cref="Deferred"/> file carries none; the file its open answers does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? Length {
        get;
        init {
            if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "A length is not negative.");

            field = value;
        }
    }

    /// <summary>The content stream of a <see cref="FromStream"/> file; <see langword="null"/> for a writer.</summary>
    public Stream? Content => _content;

    /// <summary>Writes the content to <paramref name="destination"/>: copies the stream, or runs the writer.</summary>
    /// <param name="destination">The stream to write to; not disposed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The file is <see cref="Deferred"/>; open it first.</exception>
    public Task WriteToAsync(Stream destination, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(destination);
        if (_open is not null)
            throw new InvalidOperationException("A deferred file has no content of its own; write the file OpenAsync answers.");

        return _writer is not null
            ? _writer(destination, cancellationToken)
            : _content!.CopyToAsync(destination, cancellationToken);
    }

    // RFC 9110 etagc without obs-text: '!' and '#' to '~'.
    private static bool IsOpaqueTag(string value) {
        if (value.Length == 0) return false;

        foreach (var c in value)
            if (c is not ('!' or >= '#' and <= '~'))
                return false;

        return true;
    }

    /// <summary>Disposes the content stream and its owner; idempotent.</summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try {
            if (_content is not null) await _content.DisposeAsync().ConfigureAwait(false);
        }
        finally {
            if (_owner is not null) await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
