using System.Linq.Expressions;
using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Composes a data-level authorization predicate into an <see cref="IQueryable{T}"/> so the database filters
/// the rows the current principal may access. Call this on the query <b>before</b>
/// <c>ToKeysetPageAsync</c>/<c>ToOffsetPageAsync</c>: the predicate becomes part of the single SQL statement,
/// so pagination and total counts stay correct and the database never returns rows the caller cannot see.
/// </summary>
public static class QueryableAuthorizationExtensions {
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
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(user);

        var filter = authorizer.GetFilter(user, operation ?? ResourceOperation.Read);
        return filter is null ? source : source.Where(filter);
    }

    /// <summary>
    /// Restricts <paramref name="source"/> to the rows whose <b>owner</b> the caller may access — for data that points at
    /// a resource by id (a note's <c>OwnerId</c>, a file's <c>ContactId</c>) and is visible exactly when that resource
    /// is. The owner's filter runs as a subquery in the same SQL statement:
    /// <c>WHERE owner_key IN (SELECT id FROM owners WHERE &lt;owner filter&gt;)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pointer shared by several owner types (an <c>OwnerKind</c> next to the <c>OwnerId</c>) must be narrowed to one
    /// kind <b>before</b> this call: otherwise a note of another kind whose id happens to equal a visible owner's id
    /// passes. Filter each kind by its own owner and <c>Concat</c> the queries.
    /// </para>
    /// <para>
    /// For a nullable pointer, type the owner key the same way (<c>c =&gt; (Guid?)c.Id</c>). A row pointing at no owner
    /// is excluded while the owner type is restricted, and kept when its authorizer is unrestricted for the caller.
    /// </para>
    /// </remarks>
    /// <param name="source">The referencing rows.</param>
    /// <param name="ownerKey">The referencing row's pointer to its owner (<c>n =&gt; n.OwnerId</c>).</param>
    /// <param name="owners">The owners' set (<c>db.Contacts</c>).</param>
    /// <param name="ownerId">The owner's key the pointer matches (<c>c =&gt; c.Id</c>).</param>
    /// <param name="authorizer">The owner type's authorizer.</param>
    /// <param name="user">The caller.</param>
    /// <param name="operation">The operation on the owner; defaults to <see cref="ResourceOperation.Read"/>.</param>
    /// <typeparam name="TEntity">The referencing row type.</typeparam>
    /// <typeparam name="TOwner">The owner type.</typeparam>
    /// <typeparam name="TKey">The owner key type.</typeparam>
    /// <example>
    /// <code>
    /// var notes = db.Notes
    ///     .Where(n =&gt; n.OwnerKind == RecordKind.Contact)
    ///     .WhereAuthorizedVia(n =&gt; n.OwnerId, db.Contacts, c =&gt; c.Id, contactAccess, currentUser);
    /// </code>
    /// </example>
    public static IQueryable<TEntity> WhereAuthorizedVia<TEntity, TOwner, TKey>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TKey>> ownerKey,
        IQueryable<TOwner> owners,
        Expression<Func<TOwner, TKey>> ownerId,
        IQueryAuthorizer<TOwner> authorizer,
        ICurrentUser user,
        ResourceOperation? operation = null)
        where TEntity : class
        where TOwner : class {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ownerKey);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(user);

        // No restriction on the owner type: every referencing row is visible (an owner that does not exist is the
        // data's own consistency problem, not an authorization one).
        var filter = authorizer.GetFilter(user, operation ?? ResourceOperation.Read);
        if (filter is null) return source;

        var visibleOwnerIds = owners.Where(filter).Select(ownerId);
        // A compiler-built template keeps the generic Queryable.Contains AOT-safe (no MakeGenericMethod); its two
        // parameters are replaced by the owners' subquery and the referencing row's pointer.
        Expression<Func<IQueryable<TKey>, TKey, bool>> contains = static (ids, key) => ids.Contains(key);
        var body = new Substitution(new Dictionary<ParameterExpression, Expression> {
            [contains.Parameters[0]] = visibleOwnerIds.Expression,
            [contains.Parameters[1]] = ownerKey.Body
        }).Visit(contains.Body);
        return source.Where(Expression.Lambda<Func<TEntity, bool>>(body, ownerKey.Parameters));
    }

    private sealed class Substitution(Dictionary<ParameterExpression, Expression> replacements) : ExpressionVisitor {
        protected override Expression VisitParameter(ParameterExpression node) {
            return replacements.TryGetValue(node, out var replacement) ? replacement : base.VisitParameter(node);
        }
    }
}
