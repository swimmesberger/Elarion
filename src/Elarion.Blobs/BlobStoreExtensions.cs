using System.Runtime.CompilerServices;

namespace Elarion.Blobs;

/// <summary>
/// Ergonomic conveniences layered over the streaming <see cref="IBlobStore"/> primitives.
/// </summary>
/// <remarks>
/// These wrap <see cref="IBlobStore.SaveAsync"/> and <see cref="IBlobStore.OpenReadAsync"/> so the
/// buffered and file-based call styles do not need to live on the interface. Naming follows the
/// Azure blob client (<c>DownloadContent</c>/<c>DownloadTo</c>) for familiarity.
/// </remarks>
public static class BlobStoreExtensions {
    /// <summary>
    /// Stores a blob from an in-memory byte array.
    /// </summary>
    public static Task<BlobRef> SaveAsync(
        this IBlobStore store,
        BlobUploadRequest request,
        byte[] content,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        var hinted = request.ContentLength is null
            ? request with { ContentLength = content.Length }
            : request;
        var stream = new MemoryStream(content, false);
        return SaveAndDisposeAsync(store, hinted, stream, cancellationToken);
    }

    /// <summary>
    /// Stores a blob from a file on the local filesystem.
    /// </summary>
    /// <remarks>
    /// The file is opened as a seekable stream, so a backend that streams known-length content
    /// (such as the PostgreSQL store) writes it without buffering the whole file in memory. This is
    /// a host-side convenience and carries no backend assumption — the store only ever sees a
    /// <see cref="Stream"/>.
    /// </remarks>
    public static Task<BlobRef> SaveFromFileAsync(
        this IBlobStore store,
        BlobUploadRequest request,
        string filePath,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hinted = request.ContentLength is null
            ? request with { ContentLength = stream.Length }
            : request;
        return SaveAndDisposeAsync(store, hinted, stream, cancellationToken);
    }

    /// <summary>
    /// Retrieves a blob with its content fully buffered into memory.
    /// </summary>
    /// <returns>The buffered blob, or <c>null</c> when it does not exist.</returns>
    public static async Task<BlobContent?> DownloadContentAsync(
        this IBlobStore store,
        BlobRef blobRef,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(store);

        await using var download = await store.OpenReadAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (download is null) return null;

        var data = await ReadToArrayAsync(download, cancellationToken).ConfigureAwait(false);
        var metadata = download.Metadata;

        return new BlobContent {
            Id = metadata.Id,
            Container = metadata.Container,
            Name = metadata.Name,
            ContentType = metadata.ContentType,
            Size = metadata.Size,
            CreatedAt = metadata.CreatedAt,
            Data = data
        };
    }

    /// <summary>
    /// Reads the full content of a blob into a byte array.
    /// </summary>
    /// <returns>The content bytes, or <c>null</c> when the blob does not exist.</returns>
    public static async Task<byte[]?> ReadAllBytesAsync(
        this IBlobStore store,
        BlobRef blobRef,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(store);

        await using var download = await store.OpenReadAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (download is null) return null;

        return await ReadToArrayAsync(download, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a blob's content to the supplied destination stream.
    /// </summary>
    /// <returns><c>true</c> when the blob existed and was copied; otherwise <c>false</c>.</returns>
    public static async Task<bool> DownloadToAsync(
        this IBlobStore store,
        BlobRef blobRef,
        Stream destination,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(destination);

        await using var download = await store.OpenReadAsync(blobRef, cancellationToken).ConfigureAwait(false);
        if (download is null) return false;

        await download.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Enumerates every blob under a prefix, walking <see cref="IBlobStore.ListAsync"/> page by page —
    /// the flat (recursive) enumeration migration and backup tooling wants.
    /// </summary>
    /// <param name="store">The store to enumerate.</param>
    /// <param name="container">The container to list.</param>
    /// <param name="prefix">Only names starting with this prefix, or <c>null</c> for all.</param>
    /// <param name="state">Only blobs in this lifecycle state, or <c>null</c> for all.</param>
    /// <param name="pageSize">The page size used for the underlying listing calls.</param>
    /// <param name="cancellationToken">Token used to cancel the enumeration.</param>
    public static async IAsyncEnumerable<BlobMetadata> ListAllAsync(
        this IBlobStore store,
        string container,
        string? prefix = null,
        BlobLifecycleState? state = null,
        int pageSize = 500,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        string? continuationToken = null;
        do {
            var page = await store.ListAsync(
                new BlobListRequest {
                    Container = container,
                    Prefix = prefix,
                    State = state,
                    PageSize = pageSize,
                    ContinuationToken = continuationToken
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var blob in page.Blobs) yield return blob;

            continuationToken = page.ContinuationToken;
        } while (continuationToken is not null);
    }

    private static async Task<BlobRef> SaveAndDisposeAsync(
        IBlobStore store,
        BlobUploadRequest request,
        Stream content,
        CancellationToken cancellationToken) {
        await using (content.ConfigureAwait(false)) {
            return await store.SaveAsync(request, content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadToArrayAsync(BlobDownload download, CancellationToken cancellationToken) {
        if (download.Content is MemoryStream buffered && buffered.TryGetBuffer(out var segment))
            return segment.AsSpan().ToArray();

        var capacity = download.Metadata.Size is > 0 and <= int.MaxValue ? (int)download.Metadata.Size : 0;
        using var memory = new MemoryStream(capacity);
        await download.Content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }
}
