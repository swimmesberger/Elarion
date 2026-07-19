using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;

namespace Elarion.Paging;

/// <summary>
/// In-memory evaluation of an <see cref="IQueryAuthorizer{TEntity}"/> predicate against a single,
/// already-loaded entity — the escape hatch for a handler that needs to authorize a resource it is about to
/// write, without a database round-trip.
/// </summary>
/// <remarks>
/// Valid only for predicates that read entity fields (e.g. owner/tenant rules). A predicate that consults
/// other data (e.g. a shared-grant <c>EXISTS</c> over a grants table) cannot be evaluated in memory — use the
/// database-backed <c>IResourceAuthorizer</c> for the full, grant-aware check. The predicate is compiled per
/// call, so this suits occasional pre-write checks, not hot loops.
/// </remarks>
public static class QueryAuthorizerExtensions {
    /// <summary>
    /// Returns whether <paramref name="user"/> may access <paramref name="entity"/> for
    /// <paramref name="operation"/> (defaulting to <see cref="ResourceOperation.Read"/>). A
    /// <see langword="null"/> predicate (no restriction) returns <see langword="true"/>.
    /// </summary>
    public static bool Matches<TEntity>(
        this IQueryAuthorizer<TEntity> authorizer,
        TEntity entity,
        ICurrentUser user,
        ResourceOperation? operation = null)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(user);

        var filter = authorizer.GetFilter(user, operation ?? ResourceOperation.Read);
        return filter is null || filter.Compile()(entity);
    }
}
