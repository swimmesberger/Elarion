namespace Elarion.Abstractions;

/// <summary>
/// An open <see cref="UploadedFile"/>: its content stream and what the upload endpoint recorded about it. The caller
/// owns it and disposes it once it has read <see cref="Content"/>; disposal releases the stream and the store's
/// resources behind it.
/// </summary>
public sealed class UploadedFileContent : IAsyncDisposable {
    private readonly IAsyncDisposable? _owner;
    private int _disposed;

    /// <summary>Creates the content of an upload.</summary>
    /// <param name="content">The open content stream, positioned at its start.</param>
    /// <param name="contentType">The media type the client declared when it uploaded.</param>
    /// <param name="fileName">The file name the client sent, sanitized by the upload endpoint.</param>
    /// <param name="length">The content length in bytes.</param>
    /// <param name="owner">
    /// An optional resource whose lifetime is tied to <paramref name="content"/> (for example the store's download
    /// handle); disposed after the stream.
    /// </param>
    public UploadedFileContent(
        Stream content, string contentType, string fileName, long length, IAsyncDisposable? owner = null) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(fileName);

        Content = content;
        ContentType = contentType;
        FileName = fileName;
        Length = length;
        _owner = owner;
    }

    /// <summary>The open content stream. Read it to its end at most once; open the upload again to re-read it.</summary>
    public Stream Content { get; }

    /// <summary>The media type the client declared when it uploaded (for example <c>"text/csv"</c>).</summary>
    public string ContentType { get; }

    /// <summary>
    /// The file name the client sent, as the upload endpoint stored it (without path, quotes or control characters);
    /// <c>"upload"</c> when the client sent none. Untrusted: never use it as a path.
    /// </summary>
    public string FileName { get; }

    /// <summary>The content length in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// The SHA-256 of the content as 64 lower-case hex characters, or <see langword="null"/> when the store recorded
    /// none (ADR-0099).
    /// </summary>
    public string? ContentSha256 { get; init; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try {
            await Content.DisposeAsync().ConfigureAwait(false);
        }
        finally {
            if (_owner is not null) await _owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
