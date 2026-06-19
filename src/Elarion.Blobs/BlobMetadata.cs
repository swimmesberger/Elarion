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
}
