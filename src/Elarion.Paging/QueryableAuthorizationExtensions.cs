using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;

namespace Elarion.Paging;

/// <summary>
/// Composes a data-level authorization predicate into an <see cref="IQueryable{T}"/> so the database filters
/// the rows the current principal may access. Call this on the query <b>before</b>
/// <c>ToKeysetPageAsync</c>/<c>ToOffsetPageAsync</c>: the predicate becomes part of the single SQL statement,
/// so pagination and total counts stay correct and the database never returns rows the caller cannot see.
/// </summary>
public static class QueryableAuthorizationExtensions
{
    /// <summary>
    /// Restricts <paramref name="source"/> to the rows <paramref name="user"/> may access for
    /// <paramref name="operation"/> (defaulting to <see cref="ResourceOperation.Read"/>), using
    /// <paramref name="authorizer"/>'s predicate. A <see langword="null"/> predicate (no restriction) leaves
    /// the query unchanged; a deny-all predicate yields an empty result.
    /// </summary>
    public static IQueryable<TEntity> WhereAuthorized<TEntity>(
        this IQueryable<TEntity> source,
        IQueryAuthorizer<TEntity> authorizer,
        ICurrentUser user,
        ResourceOperation? operation = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(user);

        var filter = authorizer.GetFilter(user, operation ?? ResourceOperation.Read);
        return filter is null ? source : source.Where(filter);
    }
}
