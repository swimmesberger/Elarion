namespace Elarion.Blobs;

/// <summary>
/// Describes one page of a blob listing.
/// </summary>
/// <remarks>
/// Follows the industry listing model (S3 <c>ListObjectsV2</c>, Azure <c>GetBlobsByHierarchy</c>, GCS):
/// a flat namespace with optional prefix/delimiter emulation of hierarchy. Directories are never real
/// objects — a "directory" is just a common name prefix — so there are no empty folders and no subtree
/// renames; an application that needs true folder semantics models folders as entities in its own
/// database and keeps blobs flat.
/// </remarks>
public sealed record BlobListRequest {
    /// <summary>The container to list.</summary>
    public required string Container { get; init; }

    /// <summary>Only blobs whose name starts with this prefix are returned, or <c>null</c> for all.</summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// The hierarchy delimiter (typically <c>"/"</c>), or <c>null</c> for a flat (recursive) listing.
    /// When set, names containing the delimiter beyond <see cref="Prefix"/> are rolled up into
    /// <see cref="BlobListing.Prefixes"/> — one entry per virtual directory — and only blobs at the
    /// current level appear in <see cref="BlobListing.Blobs"/>.
    /// </summary>
    public string? Delimiter { get; init; }

    /// <summary>
    /// Only blobs in this lifecycle state are returned, or <c>null</c> for all states.
    /// </summary>
    /// <remarks>
    /// On a backend without server-side state filtering (for example Azure, where the state lives in
    /// blob metadata) the filter is applied per page after listing, so a page may carry fewer than
    /// <see cref="PageSize"/> items — or none — while <see cref="BlobListing.ContinuationToken"/> still
    /// indicates more. Callers loop on the token, not on page fill.
    /// </remarks>
    public BlobLifecycleState? State { get; init; }

    /// <summary>The maximum number of entries (blobs plus prefixes) per page. Defaults to 500.</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>
    /// The opaque token from the previous page's <see cref="BlobListing.ContinuationToken"/>, or
    /// <c>null</c> for the first page. Tokens are store-specific and not portable across backends.
    /// </summary>
    public string? ContinuationToken { get; init; }
}
