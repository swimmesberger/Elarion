using System.Linq.Expressions;
using Elarion.Abstractions.Paging;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Paging;

/// <summary>
/// Executes keyset (seek) and offset pagination against an EF Core <see cref="IQueryable{T}"/>,
/// producing the transport-neutral <see cref="Page{T}"/> envelope. Keyset is the default for feeds
/// and large lists; offset adds a total count and random page access where a UI needs them.
/// </summary>
public static class QueryablePagingExtensions
{
    /// <summary>The default maximum page size applied when a caller does not specify one.</summary>
    public const int DefaultMaxSize = 100;

    private const int FallbackSize = 20;

    /// <summary>
    /// Reads one keyset page. The query is ordered by the keyset, optionally seeked past the request
    /// cursor, and one extra row is fetched to determine whether a further page exists. Projection runs
    /// server-side via the generated keyset, so only <paramref name="selector"/>'s columns and the key
    /// columns are read and the query is not tracked; boundary cursors are encoded from the key columns.
    /// </summary>
    /// <exception cref="MalformedCursorException">
    /// The request's <c>After</c>/<c>Before</c> cursor is malformed or was minted by a different keyset
    /// (identity-tag mismatch). This is a client error and is surfaced rather than silently paging from
    /// the first row; map it to a validation / <c>400</c>-style response.
    /// </exception>
    public static async Task<Page<TDto>> ToKeysetPageAsync<TEntity, TDto>(
        this IQueryable<TEntity> source,
        IKeysetPageRequest request,
        IKeysetDefinition<TEntity> keyset,
        Expression<Func<TEntity, TDto>> selector,
        int maxSize = DefaultMaxSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keyset);
        ArgumentNullException.ThrowIfNull(selector);

        var size = NormalizeSize(request.Size, maxSize);
        var backward = !string.IsNullOrEmpty(request.Before) && string.IsNullOrEmpty(request.After);
        var forward = !backward;
        var cursor = backward ? request.Before : request.After;
        var pagedFromCursor = !string.IsNullOrEmpty(cursor);

        IQueryable<TEntity> query = keyset.ApplyOrder(source, forward);
        if (pagedFromCursor)
        {
            // A malformed or wrong-keyset cursor throws MalformedCursorException; it is a client error,
            // never a silent fallback to the first page (which would return unrelated rows).
            var seek = keyset.BuildSeek(cursor!, forward);
            query = query.Where(seek);
        }

        // Fetch one extra row to detect a further page in the paging direction.
        var entries = await keyset
            .ToEntriesAsync(query.Take(size + 1), selector, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = entries.Count > size;
        var taken = hasMore ? entries.Take(size).ToList() : [.. entries];

        // Backward pages are read in reversed order; restore natural order for the caller.
        if (backward)
        {
            taken.Reverse();
        }

        if (taken.Count == 0)
        {
            return Page<TDto>.Empty;
        }

        var items = new TDto[taken.Count];
        for (var i = 0; i < taken.Count; i++)
        {
            items[i] = taken[i].Item;
        }

        return new Page<TDto>
        {
            Items = items,
            StartCursor = taken[0].Cursor,
            EndCursor = taken[^1].Cursor,
            HasNext = forward ? hasMore : pagedFromCursor,
            HasPrevious = backward ? hasMore : pagedFromCursor,
        };
    }

    /// <summary>
    /// Reads one offset page, including a total count. The query is ordered by
    /// <paramref name="sort"/> (an AOT-safe whitelist), then skipped/taken and projected server-side.
    /// </summary>
    public static async Task<Page<TDto>> ToOffsetPageAsync<TEntity, TDto>(
        this IQueryable<TEntity> source,
        IOffsetPageRequest request,
        Expression<Func<TEntity, TDto>> selector,
        SortMap<TEntity> sort,
        int maxSize = DefaultMaxSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(sort);

        var size = NormalizeSize(request.Size, maxSize);
        var page = request.Page < 1 ? 1 : request.Page;
        // Computed in long and clamped: a hostile Page near int.MaxValue would otherwise wrap the
        // multiplication negative and Skip a garbage offset. The clamped value simply lands past the
        // data, yielding the same empty page any other out-of-range page number produces.
        var skip = (int)Math.Min((long)(page - 1) * size, int.MaxValue);

        var total = await source.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await sort.Apply(source, request.Sort)
            .Skip(skip)
            .Take(size)
            .Select(selector)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Page<TDto>
        {
            Items = items,
            Total = total,
            HasPrevious = page > 1,
            HasNext = (long)skip + items.Count < total,
        };
    }

    private static int NormalizeSize(int requested, int maxSize)
    {
        if (maxSize < 1)
        {
            maxSize = 1;
        }

        if (requested < 1)
        {
            return Math.Min(FallbackSize, maxSize);
        }

        return Math.Min(requested, maxSize);
    }
}
