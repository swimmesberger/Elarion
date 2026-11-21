namespace Elarion.Blobs;

/// <summary>
/// Read-only metadata about a stored blob.
/// </summary>
public sealed record BlobMetadata {
    /// <summary>
    /// Gets the unique blob identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the logical grouping that contains the blob.
    /// </summary>
    public required string Container { get; init; }

    /// <summary>
    /// Gets the human-readable blob name within the container.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the blob MIME type.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Gets the content length in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the blob was stored.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets the blob's lifecycle state — most useful when listing, so a browse surface can distinguish
    /// (or filter out) half-finished pending uploads from committed blobs.
    /// </summary>
    public required BlobLifecycleState State { get; init; }

    /// <summary>
    /// Gets the id of the principal that owns the blob, or <c>null</c> when the blob is unowned.
    /// </summary>
    /// <remarks>
    /// Recorded from <see cref="BlobUploadRequest.OwnerId"/> at save time. Owner-scoped operations compare
    /// this exactly, so an id containing an upload transport's naming separator can never be forged.
    /// </remarks>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Gets the SHA-256 of the blob's content as 64 lower-case hex characters, or <c>null</c> when the store recorded
    /// none.
    /// </summary>
    /// <remarks>
    /// Stores compute it while <see cref="IBlobStore.SaveAsync"/> streams the content, so a caller that needs it — to
    /// find the same file uploaded twice, or as an entity tag — never reads the content a second time. It is
    /// <c>null</c> for a blob stored before its store recorded hashes, for one written past the store (another
    /// producer of the same Azure container), and for a staged upload that the Azure staged-upload store completes by
    /// a server-side copy. <see cref="BlobStoreExtensions.GetContentSha256Async"/> answers in every case, reading the
    /// content only when no hash is recorded.
    /// </remarks>
    public string? ContentSha256 { get; init; }
}
