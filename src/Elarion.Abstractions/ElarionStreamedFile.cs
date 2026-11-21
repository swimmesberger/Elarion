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
/// The instance owns its content: the HTTP layer disposes it with the response. A caller that invokes the handler
/// in process (a test, a module API) disposes it.
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
/// </code>
/// </example>
public sealed record ElarionStreamedFile : IAsyncDisposable {
    private readonly Stream? _content;
    private readonly IAsyncDisposable? _owner;
    private readonly Func<Stream, CancellationToken, Task>? _writer;
    private int _disposed;

    private ElarionStreamedFile(
        string contentType,
        Stream? content,
        IAsyncDisposable? owner,
        Func<Stream, CancellationToken, Task>? writer) {
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        ContentType = contentType;
        _content = content;
        _owner = owner;
        _writer = writer;
    }

    /// <summary>
    /// A file whose bytes are <paramref name="content"/>, copied to the response from its current position. The
    /// content stream, and <paramref name="owner"/> after it, are disposed with the response.
    /// </summary>
    /// <param name="content">The content; a seekable stream also lets the response state its length.</param>
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

    /// <summary>The MIME content type.</summary>
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

    /// <summary>The content stream of a <see cref="FromStream"/> file; <see langword="null"/> for a writer.</summary>
    public Stream? Content => _content;

    /// <summary>Writes the content to <paramref name="destination"/>: copies the stream, or runs the writer.</summary>
    /// <param name="destination">The stream to write to; not disposed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteToAsync(Stream destination, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(destination);
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
