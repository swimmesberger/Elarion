namespace Elarion.Blobs;

/// <summary>
/// One page of a blob listing: the blobs at the requested level and, when a delimiter was supplied,
/// the virtual directories below it.
/// </summary>
public sealed record BlobListing {
    /// <summary>The blobs on this page, in lexicographic (ordinal) name order.</summary>
    public required IReadOnlyList<BlobMetadata> Blobs { get; init; }

    /// <summary>
    /// The virtual directories on this page — distinct name segments from the request's prefix up to
    /// and including the delimiter (for example <c>"docs/"</c>). Empty when the request carried no
    /// delimiter.
    /// </summary>
    public required IReadOnlyList<string> Prefixes { get; init; }

    /// <summary>
    /// The opaque token for the next page, or <c>null</c> when the listing is exhausted.
    /// </summary>
    public string? ContinuationToken { get; init; }
}
