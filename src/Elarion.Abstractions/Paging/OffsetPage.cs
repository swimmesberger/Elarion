namespace Elarion.Abstractions.Paging;

/// <summary>
/// A single page of an offset (page-number) list, transport-neutral: its items, the total number of matching items,
/// and whether a page exists on either side. Offset paging always counts, so <see cref="Total"/> is required and the
/// envelope carries no cursors — the generated TypeScript client types <c>total</c> as a plain <c>number</c>.
/// Keyset lists return <see cref="Page{T}"/>.
/// </summary>
/// <typeparam name="T">The item type (typically a response DTO).</typeparam>
public sealed record OffsetPage<T> {
    /// <summary>The items in this page, in result order.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Total number of matching items across all pages.</summary>
    public required int Total { get; init; }

    /// <summary>Whether a page exists after this one.</summary>
    public bool HasNext { get; init; }

    /// <summary>Whether a page exists before this one.</summary>
    public bool HasPrevious { get; init; }

    /// <summary>An empty first page: no items and a total of zero.</summary>
    public static OffsetPage<T> Empty { get; } = new() { Items = [], Total = 0 };

    /// <summary>
    /// Projects every item with <paramref name="selector"/>, keeping the page's total and flags — the mapping a handler
    /// needs after paging an entity query into its response DTOs.
    /// </summary>
    /// <example>
    /// <code>
    /// var page = await db.Clients.ToOffsetPageAsync(request, c =&gt; c, Sort, ct: ct);
    /// return page.Map(c =&gt; new ClientRow(c.Id, c.Name));
    /// </code>
    /// </example>
    public OffsetPage<TResult> Map<TResult>(Func<T, TResult> selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var items = new TResult[Items.Count];
        for (var i = 0; i < items.Length; i++) items[i] = selector(Items[i]);

        return WithItems(items);
    }

    /// <summary>
    /// Projects the whole item list at once — one batched lookup for every row of the page instead of one per row —
    /// keeping the page's total and flags. <paramref name="selector"/> must return exactly one result per item, in item
    /// order.
    /// </summary>
    /// <exception cref="InvalidOperationException">The selector returned a different number of items.</exception>
    public async ValueTask<OffsetPage<TResult>> MapAsync<TResult>(
        Func<IReadOnlyList<T>, CancellationToken, ValueTask<IReadOnlyList<TResult>>> selector,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(selector);

        var items = await selector(Items, ct).ConfigureAwait(false);
        if (items.Count != Items.Count)
            throw new InvalidOperationException(
                $"The page selector returned {items.Count} items for a page of {Items.Count}; it must return one result per item.");

        return WithItems(items);
    }

    /// <summary>
    /// The same page as the shared <see cref="Page{T}"/> envelope (no cursors, <see cref="Page{T}.Total"/> set), for an
    /// endpoint whose contract stays <see cref="Page{T}"/>.
    /// </summary>
    public Page<T> ToPage() {
        return new Page<T> { Items = Items, Total = Total, HasNext = HasNext, HasPrevious = HasPrevious };
    }

    private OffsetPage<TResult> WithItems<TResult>(IReadOnlyList<TResult> items) {
        return new OffsetPage<TResult> { Items = items, Total = Total, HasNext = HasNext, HasPrevious = HasPrevious };
    }
}
