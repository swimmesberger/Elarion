namespace Elarion.Blobs;

/// <summary>
/// Stores and retrieves binary content.
/// </summary>
public interface IBlobStore {
    /// <summary>
    /// Stores a blob and returns a lightweight reference to it.
    /// </summary>
    /// <param name="container">Logical grouping for the blob.</param>
    /// <param name="name">Human-readable name within the container.</param>
    /// <param name="contentType">MIME type for the content.</param>
    /// <param name="content">Raw content bytes to store.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A reference to the stored blob.</returns>
    Task<BlobRef> SaveAsync(
        string container,
        string name,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a blob from a file on the local filesystem.
    /// </summary>
    /// <param name="container">Logical grouping for the blob.</param>
    /// <param name="name">Human-readable name within the container.</param>
    /// <param name="contentType">MIME type for the content.</param>
    /// <param name="filePath">Absolute path to the source file.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A reference to the stored blob.</returns>
    Task<BlobRef> SaveFromFileAsync(
        string container,
        string name,
        string contentType,
        string filePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the full blob.
    /// </summary>
    /// <param name="blobRef">Reference to the blob.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The blob content, or <c>null</c> when it does not exist.</returns>
    Task<BlobContent?> GetAsync(BlobRef blobRef, CancellationToken cancellationToken);

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
}
