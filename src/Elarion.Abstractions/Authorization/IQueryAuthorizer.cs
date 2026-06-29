using System.Linq.Expressions;
using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Produces a data-level authorization predicate for <typeparamref name="TEntity"/> — the set of rows the
/// current principal may access for a given <see cref="ResourceOperation"/>. Composed into an
/// <c>IQueryable&lt;TEntity&gt;</c> (via <c>WhereAuthorized</c>) <b>before</b> paging, so the database filters
/// during query planning rather than the application checking rows after they are fetched (the broken,
/// non-scaling "filter in memory after the query" pattern).
/// </summary>
/// <remarks>
/// Implementations are typically emitted by the Elarion source generator from a
/// <c>[ResourceFilter&lt;TEntity&gt;]</c> attribute and contain no reflection on the query path — the predicate
/// is a plain typed expression the query provider translates to SQL. The seam is intentionally narrow so an
/// alternative backend (an external relationship/policy engine) could implement it without touching callers.
/// </remarks>
/// <typeparam name="TEntity">The entity type being authorized.</typeparam>
public interface IQueryAuthorizer<TEntity>
    where TEntity : class {
    /// <summary>
    /// Returns the predicate selecting the rows <paramref name="user"/> may access for
    /// <paramref name="operation"/>:
    /// <list type="bullet">
    /// <item><see langword="null"/> means <b>no restriction</b> (e.g. an administrator) — the query is left unfiltered.</item>
    /// <item>A constant-<see langword="false"/> predicate (<c>_ =&gt; false</c>) means <b>deny all</b> — the
    /// fail-closed result when the principal is unauthenticated or lacks any access.</item>
    /// </list>
    /// </summary>
    Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation);
}
