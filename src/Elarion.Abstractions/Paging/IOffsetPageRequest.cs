namespace Elarion.Abstractions.Paging;

/// <summary>
/// An offset (skip/take) pagination request. Implemented by handler request DTOs so the pagination
/// fields stay flat for HTTP GET query-string binding. Offset pagination supports random page
/// access and a total count, at the cost of degrading performance over large offsets — prefer
/// keyset pagination (<see cref="IKeysetPageRequest"/>) for feeds and large lists.
/// </summary>
public interface IOffsetPageRequest {
    /// <summary>1-based page number.</summary>
    int Page { get; }

    /// <summary>Requested page size. The execution helper clamps this to a configured maximum.</summary>
    int Size { get; }

    /// <summary>
    /// Optional sort key. Resolved against a handler-supplied whitelist (see <c>SortMap</c>);
    /// a leading <c>-</c> requests descending order (e.g. <c>"-createdAt"</c>). Unknown or blank
    /// keys fall back to the whitelist's default sort.
    /// </summary>
    string? Sort { get; }
}
