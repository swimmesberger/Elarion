using System.Linq.Expressions;
using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Composes <see cref="IQueryAuthorizer{TEntity}"/> instances, so a rule the generated <c>[ResourceFilter]</c> cannot
/// express — sharing through an application-owned membership table, a visibility computed elsewhere — is added to it
/// instead of replacing it.
/// </summary>
/// <remarks>
/// A <see langword="null"/> filter means "no restriction": it wins an <see cref="AnyOf{TEntity}"/> and is neutral in an
/// <see cref="AllOf{TEntity}"/>. The combined predicate is one expression over one parameter, so it still translates to
/// a single SQL <c>WHERE</c>.
/// </remarks>
/// <example>
/// <code>
/// // Contacts are visible to their owner and shared grants (generated), or to members of a group they are shared with.
/// services.AddScoped&lt;IQueryAuthorizer&lt;Contact&gt;&gt;(sp =&gt; {
///     var db = sp.GetRequiredService&lt;AppDbContext&gt;();
///     return QueryAuthorizers.AnyOf(
///         new ContactAccess(sp.GetRequiredService&lt;IResourceGrantSource&gt;()),
///         QueryAuthorizers.From&lt;Contact&gt;((user, _) =&gt; c =&gt; db.ContactGroupShares.Any(s =&gt;
///             s.ContactId == c.Id &amp;&amp; db.GroupMembers.Any(m =&gt; m.GroupId == s.GroupId &amp;&amp; m.UserId == user.UserId))));
/// });
/// </code>
/// </example>
public static class QueryAuthorizers {
    /// <summary>An authorizer from a delegate producing the filter for a caller and operation.</summary>
    public static IQueryAuthorizer<TEntity> From<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(filter);
        return new DelegateAuthorizer<TEntity>(filter);
    }

    /// <summary>A row is visible when <b>any</b> authorizer makes it visible (OR); one unrestricted authorizer lifts the restriction.</summary>
    /// <exception cref="ArgumentException">An authorizer is <see langword="null"/>.</exception>
    public static IQueryAuthorizer<TEntity> AnyOf<TEntity>(params IQueryAuthorizer<TEntity>[] authorizers)
        where TEntity : class {
        var snapshot = Snapshot(authorizers);
        return new DelegateAuthorizer<TEntity>((user, operation) => {
            Expression<Func<TEntity, bool>>? combined = null;
            foreach (var authorizer in snapshot) {
                var filter = authorizer.GetFilter(user, operation);
                if (filter is null) return null;

                combined = combined is null ? filter : Combine(combined, filter, Expression.OrElse);
            }

            return combined ?? (_ => false);
        });
    }

    /// <summary>A row is visible when <b>every</b> authorizer makes it visible (AND); an unrestricted authorizer adds nothing.</summary>
    /// <exception cref="ArgumentException">
    /// No authorizer is given (an empty AND would be unrestricted, which fails open), or one is <see langword="null"/>.
    /// </exception>
    public static IQueryAuthorizer<TEntity> AllOf<TEntity>(params IQueryAuthorizer<TEntity>[] authorizers)
        where TEntity : class {
        var snapshot = Snapshot(authorizers);
        if (snapshot.Length == 0)
            throw new ArgumentException("AllOf needs at least one authorizer; an empty AND would restrict nothing.",
                nameof(authorizers));
        return new DelegateAuthorizer<TEntity>((user, operation) => {
            Expression<Func<TEntity, bool>>? combined = null;
            foreach (var authorizer in snapshot)
                if (authorizer.GetFilter(user, operation) is { } filter)
                    combined = combined is null ? filter : Combine(combined, filter, Expression.AndAlso);

            return combined;
        });
    }

    // A copy, so a caller reusing the params array cannot change the composition afterwards; a null element is refused
    // here rather than failing on the first query.
    private static IQueryAuthorizer<TEntity>[] Snapshot<TEntity>(IQueryAuthorizer<TEntity>[] authorizers)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(authorizers);
        var snapshot = authorizers.ToArray();
        if (Array.IndexOf(snapshot, null) >= 0)
            throw new ArgumentException("An authorizer is null.", nameof(authorizers));
        return snapshot;
    }

    // Rebinds the right predicate onto the left one's parameter, so the result is one lambda a provider translates.
    private static Expression<Func<TEntity, bool>> Combine<TEntity>(
        Expression<Func<TEntity, bool>> left,
        Expression<Func<TEntity, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine) {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<TEntity, bool>>(combine(left.Body, rightBody), parameter);
    }

    private sealed class DelegateAuthorizer<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter) : IQueryAuthorizer<TEntity>
        where TEntity : class {
        public Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return filter(user, operation);
        }
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor {
        protected override Expression VisitParameter(ParameterExpression node) {
            return node == from ? to : base.VisitParameter(node);
        }
    }
}
