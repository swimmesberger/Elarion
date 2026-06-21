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
}
