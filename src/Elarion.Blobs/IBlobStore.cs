namespace Elarion.Blobs;

/// <summary>
/// Stores and retrieves binary content.
/// </summary>
/// <remarks>
/// The interface is streaming-first: content flows in and out as <see cref="Stream"/> so callers
/// are never required to buffer a whole blob in memory. Ergonomic <c>byte[]</c>, file, buffered,
/// and copy-to conveniences are provided by <see cref="BlobStoreExtensions"/> over this minimal
/// core, so every backend gets them for free and a new backend implements only the primitives.
/// </remarks>
public interface IBlobStore {
    /// <summary>
    /// Stores a blob from a content stream and returns a lightweight reference to it.
    /// </summary>
    /// <param name="request">Identity and metadata for the blob being stored.</param>
    /// <param name="content">
    /// The content to store. Read to its end; the store records the actual number of bytes read as
    /// <see cref="BlobMetadata.Size"/> regardless of <see cref="BlobUploadRequest.ContentLength"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A reference to the stored blob.</returns>
    Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored blob for streaming reads.
    /// </summary>
    /// <param name="blobRef">Reference to the blob.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A disposable <see cref="BlobDownload"/> carrying the metadata and an open content stream, or
    /// <c>null</c> when the blob does not exist. The caller must dispose the result.
    /// </returns>
    Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves metadata without loading the blob content.
    /// </summary>
    /// <param name="blobRef">Reference to the blob.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The blob metadata, or <c>null</c> when it does not exist.</returns>
    Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a blob by reference.
    /// </summary>
    /// <param name="blobRef">Reference to the blob.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when the blob existed and was deleted; otherwise <c>false</c>.</returns>
    Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a blob exists.
    /// </summary>
    /// <param name="blobRef">Reference to the blob.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when the blob exists; otherwise <c>false</c>.</returns>
    Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken);

    /// <summary>
    /// Lists one page of blobs, optionally rolled up into virtual directories by a delimiter (the
    /// S3/Azure prefix-plus-delimiter model — see <see cref="BlobListRequest"/>).
    /// </summary>
    /// <param name="request">Container, prefix/delimiter, state filter, and paging inputs.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// The page, with entries in lexicographic (ordinal) name order. A missing container yields an
    /// empty page, not an error. Listing is a browse/ops surface (admin UIs, migration and backup
    /// tooling) — an application queries its own entities for "which blobs belong to X", it does not
    /// enumerate the store.
    /// </returns>
    Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the containers known to the store, in ascending name order.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The container names.</returns>
    Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken);
}
