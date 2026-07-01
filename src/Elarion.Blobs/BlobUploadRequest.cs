namespace Elarion.Blobs;

/// <summary>
/// Describes a blob to store. Content is supplied separately as a stream so callers are never
/// required to materialize it in memory.
/// </summary>
/// <remarks>
/// Modeled on the request objects used by the major blob SDKs (for example AWS S3
/// <c>PutObjectRequest</c> and Azure <c>BlobUploadOptions</c>) so additional upload concerns
/// (custom metadata, tags, content encoding) can be added here later without new overloads.
/// </remarks>
public sealed record BlobUploadRequest {
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
    /// Gets the optional content length hint, in bytes.
    /// </summary>
    /// <remarks>
    /// This is a hint only: stores may use it to optimize the write (for example to avoid
    /// buffering a non-seekable stream), but the recorded <see cref="BlobMetadata.Size"/> is always
    /// the actual number of bytes written. Leave <c>null</c> when the length is unknown up front,
    /// mirroring how S3/Azure/GCS derive the length rather than demand it.
    /// </remarks>
    public long? ContentLength { get; init; }

    /// <summary>
    /// Gets the lifecycle state the blob is created in. Defaults to
    /// <see cref="BlobLifecycleState.Committed"/> so a plain save produces a permanent blob.
    /// </summary>
    /// <remarks>
    /// Upload transports that pre-upload a file before it is referenced set this to
    /// <see cref="BlobLifecycleState.Pending"/> together with <see cref="ExpiresAt"/>, so the blob is
    /// reclaimed by garbage collection unless an application commits it via
    /// <see cref="IBlobLifecycle.CommitAsync"/>.
    /// </remarks>
    public BlobLifecycleState InitialState { get; init; } = BlobLifecycleState.Committed;

    /// <summary>
    /// Gets the instant after which a <see cref="BlobLifecycleState.Pending"/> blob may be garbage
    /// collected, or <c>null</c> for no expiry.
    /// </summary>
    /// <remarks>
    /// Meaningful only when <see cref="InitialState"/> is <see cref="BlobLifecycleState.Pending"/>;
    /// stores clear it when the blob is committed.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Gets the id of the principal that owns the blob, or <c>null</c> when the blob is unowned.
    /// </summary>
    /// <remarks>
    /// Upload transports record the uploading user here so an owner-scoped operation (for example a cancel)
    /// compares against a dedicated value rather than parsing it out of <see cref="Name"/>. Ownership is
    /// stored and compared exactly, so an id containing the transport's naming separator can never be
    /// forged. It is surfaced back as <see cref="BlobMetadata.OwnerId"/>.
    /// </remarks>
    public string? OwnerId { get; init; }
}
