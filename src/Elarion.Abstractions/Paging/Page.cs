namespace Elarion.Abstractions.Paging;

/// <summary>
/// A single page of results, transport-neutral and shared by every list handler so that
/// keyset and offset pagination expose one uniform shape across JSON-RPC, MCP, HTTP, and the
/// generated TypeScript client.
/// </summary>
/// <typeparam name="T">The item type (typically a response DTO).</typeparam>
/// <remarks>
/// Cursors follow the Relay convention: <see cref="StartCursor"/> and <see cref="EndCursor"/>
/// are opaque tokens identifying the first and last items, used to request the previous/next page.
/// They are populated for keyset pagination and <c>null</c> for offset pagination, where
/// <see cref="Total"/> is populated instead.
/// </remarks>
public sealed record Page<T> {
    /// <summary>The items in this page, in result order.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Opaque cursor for the first item. <c>null</c> when the page is empty or for offset pagination.</summary>
    public string? StartCursor { get; init; }

    /// <summary>Opaque cursor for the last item. <c>null</c> when the page is empty or for offset pagination.</summary>
    public string? EndCursor { get; init; }

    /// <summary>Whether a page exists after this one.</summary>
    public bool HasNext { get; init; }

    /// <summary>Whether a page exists before this one.</summary>
    public bool HasPrevious { get; init; }

    /// <summary>
    /// Total number of matching items across all pages. Populated only for offset pagination;
    /// <c>null</c> for keyset pagination, which cannot produce a total cheaply.
    /// </summary>
    public int? Total { get; init; }

    /// <summary>An empty page (no items, no cursors).</summary>
    public static Page<T> Empty { get; } = new() { Items = [] };
}
