namespace Elarion.Blobs;

/// <summary>
/// A streaming read handle for a stored blob: its metadata together with an open content stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the pull-based read primitive, mirroring Azure <c>BlobDownloadStreamingResult</c> and
/// the AWS S3 <c>GetObjectResponse</c>. The caller owns the lifetime and must dispose the handle
/// once it has finished reading <see cref="Content"/>; disposal releases the stream and any backend
/// resources (such as an open data reader and command) that back it.
/// </para>
/// <para>
/// A buffered implementation passes an in-memory stream and no owned resource. A streaming
/// implementation passes the backend's read stream plus the reader/command as
/// <paramref name="ownedResource"/>, so those live exactly as long as the caller reads.
/// </para>
/// </remarks>
public sealed class BlobDownload : IAsyncDisposable {
    private readonly IAsyncDisposable? _ownedResource;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobDownload"/> class.
    /// </summary>
    /// <param name="metadata">Metadata for the stored blob.</param>
    /// <param name="content">An open stream positioned at the start of the blob content.</param>
    /// <param name="ownedResource">
    /// An optional backend resource (for example a data reader and command) whose lifetime is tied
    /// to <paramref name="content"/>. Disposed after the content stream.
    /// </param>
    public BlobDownload(BlobMetadata metadata, Stream content, IAsyncDisposable? ownedResource = null) {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        Metadata = metadata;
        Content = content;
        _ownedResource = ownedResource;
    }

    /// <summary>
    /// Gets the metadata for the stored blob.
    /// </summary>
    public BlobMetadata Metadata { get; }

    /// <summary>
    /// Gets the open content stream. The caller reads from this stream and disposes the owning
    /// <see cref="BlobDownload"/> when finished.
    /// </summary>
    public Stream Content { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await Content.DisposeAsync().ConfigureAwait(false);
        if (_ownedResource is not null) {
            await _ownedResource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
