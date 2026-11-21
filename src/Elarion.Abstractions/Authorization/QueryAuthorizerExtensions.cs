using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// In-memory evaluation of an <see cref="IQueryAuthorizer{TEntity}"/> predicate against a single,
/// already-loaded entity — the escape hatch for a handler that needs to authorize a resource it is about to
/// write, without a database round-trip.
/// </summary>
/// <remarks>
/// <para>
/// Valid only for predicates that read the entity's own scalar fields (owner and tenant rules). A predicate that
/// consults other data is <b>not</b> answered correctly here: a subquery over another set (a shared-grant
/// <c>EXISTS</c>, a membership table) runs as a separate synchronous query per call, and a navigation the entity
/// was loaded without (<c>c.Shares.Any(…)</c>) is simply empty — which denies a positive rule and <b>allows</b> a
/// negated one. For such a rule check the row in the database instead:
/// <c>db.Contacts.WhereAuthorized(access, user).AnyAsync(c =&gt; c.Id == id)</c>, or <c>[RequireResource]</c> with the
/// entity's filter check registered.
/// </para>
/// <para>The predicate is compiled per call, so this suits occasional pre-write checks, not hot loops.</para>
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
