using System.Linq.Expressions;

namespace Elarion.Paging;

/// <summary>
/// A single projected row paired with its opaque keyset cursor.
/// </summary>
/// <typeparam name="TDto">The projected item type.</typeparam>
/// <param name="Item">The projected item.</param>
/// <param name="Cursor">The opaque cursor identifying this row's keyset position.</param>
public sealed record KeysetEntry<TDto>(TDto Item, string Cursor);

/// <summary>
/// A compile-time keyset definition for an entity: ordering, seek predicate, and server-side
/// projection. Implementations are emitted by the Elarion source generator from a
/// <c>[Keyset&lt;TEntity&gt;]</c> attribute on a partial class and contain no reflection on the query
/// path — the ordering and seek predicate are plain
/// typed expressions the provider translates to SQL exactly like hand-written
/// <c>OrderBy</c>/<c>Where</c> clauses.
/// </summary>
/// <typeparam name="TEntity">The entity type being paginated.</typeparam>
public interface IKeysetDefinition<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Applies the keyset ordering to <paramref name="source"/>. When <paramref name="forward"/> is
    /// <c>false</c> the ordering is reversed so a "before" page can be read; the caller reverses the
    /// materialized rows back into natural order.
    /// </summary>
    IOrderedQueryable<TEntity> ApplyOrder(IQueryable<TEntity> source, bool forward);

    /// <summary>
    /// Builds the seek predicate selecting rows strictly after <paramref name="cursor"/> in the
    /// paging direction, or <c>null</c> when the cursor is malformed (treated as "no cursor").
    /// </summary>
    Expression<Func<TEntity, bool>>? BuildSeek(string cursor, bool forward);

    /// <summary>
    /// Projects <paramref name="query"/> server-side to <paramref name="selector"/>'s shape plus the
    /// keyset columns, materializes the result, and pairs each item with its opaque cursor. Only the
    /// projected columns and the key columns are read from the database, and the query is not tracked.
    /// </summary>
    Task<IReadOnlyList<KeysetEntry<TDto>>> ToEntriesAsync<TDto>(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, TDto>> selector,
        CancellationToken cancellationToken);
}
