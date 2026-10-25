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

    /// <summary>
    /// Projects every item with <paramref name="selector"/>, keeping the page's cursors, flags and total — the
    /// mapping a handler needs after paging an entity query into its response DTOs.
    /// </summary>
    /// <example>
    /// <code>
    /// var page = await db.Clients.OrderBy(c =&gt; c.Name).ToOffsetPageAsync(request, ct);
    /// return page.Map(c =&gt; new ClientRow(c.Id, c.Name));
    /// </code>
    /// </example>
    public Page<TResult> Map<TResult>(Func<T, TResult> selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var items = new TResult[Items.Count];
        for (var i = 0; i < items.Length; i++) items[i] = selector(Items[i]);

        return WithItems(items);
    }

    /// <summary>
    /// Projects the whole item list at once — one batched lookup for every row of the page instead of one per row —
    /// keeping the page's cursors, flags and total. <paramref name="selector"/> must return exactly one result per
    /// item, in item order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The selector returned a different number of items.</exception>
    /// <example>
    /// <code>
    /// return await page.MapAsync(async (rows, token) =&gt; {
    ///     var owners = await directory.GetNamesAsync(rows.Select(r =&gt; r.OwnerId), token);
    ///     return rows.Select(r =&gt; new NoteRow(r.Id, owners[r.OwnerId])).ToArray();
    /// }, ct);
    /// </code>
    /// </example>
    public async ValueTask<Page<TResult>> MapAsync<TResult>(
        Func<IReadOnlyList<T>, CancellationToken, ValueTask<IReadOnlyList<TResult>>> selector,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(selector);

        var items = await selector(Items, ct).ConfigureAwait(false);
        if (items.Count != Items.Count)
            throw new InvalidOperationException(
                $"The page selector returned {items.Count} items for a page of {Items.Count}; it must return one result per item.");

        return WithItems(items);
    }

    private Page<TResult> WithItems<TResult>(IReadOnlyList<TResult> items) {
        return new Page<TResult> {
            Items = items,
            StartCursor = StartCursor,
            EndCursor = EndCursor,
            HasNext = HasNext,
            HasPrevious = HasPrevious,
            Total = Total
        };
    }
}
