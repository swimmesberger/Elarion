using System.Globalization;
using System.Linq.Expressions;
using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Builds and composes <see cref="IQueryAuthorizer{TEntity}"/> instances, so a rule the generated
/// <c>[ResourceFilter]</c> cannot express — sharing through an application-owned membership table, rows every holder of
/// a permission may see, an administrator role — is added to it instead of replacing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Building blocks fail closed.</b> <see cref="Rows{TEntity}"/>, <see cref="Unrestricted{TEntity}"/>,
/// <see cref="ForUser{TEntity,TKey}"/> and the <c>When…</c> gates deny a caller who is not authenticated (and, for
/// <see cref="ForUser{TEntity,TKey}"/>, whose id does not parse) before the rule is consulted. Only
/// <see cref="From{TEntity}"/> hands the caller to a delegate unchecked.
/// </para>
/// <para>
/// A <see langword="null"/> filter means "no restriction": it wins an <see cref="AnyOf{TEntity}"/> and is neutral in an
/// <see cref="AllOf{TEntity}"/>. The combined predicate is one expression over one parameter, so it still translates to
/// a single SQL <c>WHERE</c>.
/// </para>
/// <para>
/// <b>A scope does not survive an OR.</b> <c>AnyOf(tenantScopedFilter, extraRule)</c> lets <c>extraRule</c> reach rows
/// of every tenant, and an unrestricted arm lifts the scope for its callers. Keep a scope outside the alternatives:
/// <c>AllOf(scope, AnyOf(…))</c> — an unrestricted alternative is then neutral and the scope still applies.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Unrestricted contacts for everyone who may read contacts, any contact for its owner, for members of a group it
/// // is shared with, and for administrators.
/// public static readonly IQueryAuthorizer&lt;Contact&gt; Visibility = QueryAuthorizers.AnyOf(
///     QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted&lt;Contact&gt;()),
///     QueryAuthorizers.WhenPermission("contacts.read", QueryAuthorizers.Rows&lt;Contact&gt;(c =&gt; !c.IsRestricted)),
///     ContactOwnerFilter.Specification,
///     QueryAuthorizers.ForUser&lt;Contact, Guid&gt;((c, userId) =&gt;
///         c.Shares.Any(s =&gt; s.Group.Members.Any(m =&gt; m.UserId == userId))));
/// </code>
/// </example>
public static class QueryAuthorizers {
    /// <summary>
    /// An authorizer from a delegate producing the filter for a caller and operation. The delegate is the whole rule:
    /// nothing checks that the caller is authenticated, and a <see langword="null"/> result lifts every restriction.
    /// Prefer the fail-closed building blocks; reach for this when none fits.
    /// </summary>
    public static IQueryAuthorizer<TEntity> From<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(filter);
        return new DelegateAuthorizer<TEntity>(filter);
    }

    /// <summary>The rows matching <paramref name="predicate"/>, for every authenticated caller; nothing for anyone else.</summary>
    /// <remarks>Gate it (<see cref="WhenPermission{TEntity}(string, IQueryAuthorizer{TEntity}, string)"/>) to say who
    /// sees those rows.</remarks>
    public static IQueryAuthorizer<TEntity> Rows<TEntity>(Expression<Func<TEntity, bool>> predicate)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DelegateAuthorizer<TEntity>((user, _) => user.IsAuthenticated ? predicate : Deny<TEntity>.Filter);
    }

    /// <summary>
    /// Every row, for every authenticated caller; nothing for anyone else. Meaningful only behind a gate — the
    /// "administrators see everything" alternative of an <see cref="AnyOf{TEntity}"/>.
    /// </summary>
    /// <remarks>
    /// Unrestricted means unrestricted: as an alternative next to a scoped filter it lifts that scope too. Put the
    /// scope outside (<c>AllOf(scope, AnyOf(…))</c>) to keep it.
    /// </remarks>
    public static IQueryAuthorizer<TEntity> Unrestricted<TEntity>()
        where TEntity : class {
        return new DelegateAuthorizer<TEntity>((user, _) => user.IsAuthenticated ? null : Deny<TEntity>.Filter);
    }

    /// <summary>
    /// The rows related to the caller by <paramref name="relation"/>, a predicate over the row and the caller's id —
    /// ownership, membership, an assignment. Denies a caller who is not authenticated or whose
    /// <see cref="ICurrentUser.UserId"/> does not parse as <typeparamref name="TKey"/>.
    /// </summary>
    /// <remarks>
    /// The rule is stated once as a relation between a row and a principal instead of being rebuilt per caller, and the
    /// id reaches the query as a parameter. The id is parsed with the invariant culture.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    /// <typeparam name="TKey">The type of the user id the rows store (<see cref="Guid"/>, <see cref="int"/>,
    /// <see cref="long"/>, <see cref="string"/>, …).</typeparam>
    /// <example>
    /// <code>
    /// QueryAuthorizers.ForUser&lt;Contact, Guid&gt;((c, userId) =&gt;
    ///     c.Shares.Any(s =&gt; s.Group.Members.Any(m =&gt; m.UserId == userId)))
    /// </code>
    /// </example>
    public static IQueryAuthorizer<TEntity> ForUser<TEntity, TKey>(Expression<Func<TEntity, TKey, bool>> relation)
        where TEntity : class
        where TKey : IParsable<TKey> {
        ArgumentNullException.ThrowIfNull(relation);
        return new DelegateAuthorizer<TEntity>((user, _) => {
            if (!user.IsAuthenticated || !TKey.TryParse(user.UserId, CultureInfo.InvariantCulture, out var userId))
                return Deny<TEntity>.Filter;

            // A captured local, so a query provider sends the id as a parameter instead of inlining a literal.
            Expression<Func<TKey>> id = () => userId;
            var body = new Rebinder(relation.Parameters[1], id.Body).Visit(relation.Body);
            return Expression.Lambda<Func<TEntity, bool>>(body, relation.Parameters[0]);
        });
    }

    /// <summary>
    /// <paramref name="then"/> for an authenticated caller satisfying <paramref name="condition"/>; nothing for anyone
    /// else. The general gate behind <see cref="WhenPermission{TEntity}(string, IQueryAuthorizer{TEntity}, string)"/>
    /// and <see cref="WhenRole{TEntity}"/>.
    /// </summary>
    public static IQueryAuthorizer<TEntity> When<TEntity>(
        Func<ICurrentUser, bool> condition, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(then);
        return new DelegateAuthorizer<TEntity>((user, operation) =>
            user.IsAuthenticated && condition(user) ? then.GetFilter(user, operation) : Deny<TEntity>.Filter);
    }

    /// <summary>
    /// <paramref name="then"/> for holders of <paramref name="permission"/>; nothing for anyone else — "holders of
    /// <c>contacts.read</c> see the contacts nobody restricted".
    /// </summary>
    /// <param name="permission">The permission, as <c>[RequirePermission]</c> spells it (<c>"{resource}.{verb}"</c>).</param>
    /// <param name="then">What a holder may access.</param>
    /// <param name="claimType">
    /// The claim type the permission is read from. Defaults to
    /// <see cref="AuthorizationOptions.DefaultPermissionClaimType"/>; pass the host's
    /// <see cref="AuthorizationOptions.PermissionClaimType"/> when it changes it.
    /// </param>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    public static IQueryAuthorizer<TEntity> WhenPermission<TEntity>(
        string permission,
        IQueryAuthorizer<TEntity> then,
        string claimType = AuthorizationOptions.DefaultPermissionClaimType)
        where TEntity : class {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        ArgumentException.ThrowIfNullOrEmpty(claimType);
        return When(user => user.HasClaim(claimType, permission), then);
    }

    /// <summary><paramref name="then"/> for callers in <paramref name="role"/>; nothing for anyone else.</summary>
    public static IQueryAuthorizer<TEntity> WhenRole<TEntity>(string role, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return When(user => user.IsInRole(role), then);
    }

    /// <summary>
    /// <paramref name="authorizer"/> for the listed operations; nothing for every other operation — so a rule that is
    /// wider for reading than for writing ("everyone sees the public rows, only the owner edits them") states which
    /// operations each alternative grants, and an operation nobody listed is denied.
    /// </summary>
    /// <exception cref="ArgumentException">No operation is given, or one is the invalid <see langword="default"/>.</exception>
    /// <example>
    /// <code>
    /// QueryAuthorizers.AnyOf(
    ///     QueryAuthorizers.ForOperations(publicRows, ResourceOperation.Read),   // read-only alternative
    ///     ownerFilter)                                                          // every operation
    /// </code>
    /// </example>
    public static IQueryAuthorizer<TEntity> ForOperations<TEntity>(
        IQueryAuthorizer<TEntity> authorizer, params ResourceOperation[] operations)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(operations);
        var allowed = operations.ToArray();
        if (allowed.Length == 0)
            throw new ArgumentException("ForOperations needs at least one operation.", nameof(operations));
        if (Array.Exists(allowed, static operation => operation.Name is null))
            throw new ArgumentException("An operation is the invalid default value.", nameof(operations));

        return new DelegateAuthorizer<TEntity>((user, operation) =>
            Array.IndexOf(allowed, operation) >= 0 ? authorizer.GetFilter(user, operation) : Deny<TEntity>.Filter);
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
                // An alternative that denies everyone adds nothing; leaving it out keeps the SQL to the live rules.
                if (IsDenyAll(filter)) continue;

                combined = combined is null ? filter : Combine(combined, filter, Expression.OrElse);
            }

            return combined ?? Deny<TEntity>.Filter;
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
            foreach (var authorizer in snapshot) {
                if (authorizer.GetFilter(user, operation) is not { } filter) continue;
                if (IsDenyAll(filter)) return filter;

                combined = combined is null ? filter : Combine(combined, filter, Expression.AndAlso);
            }

            return combined;
        });
    }

    /// <summary>Whether <paramref name="filter"/> is the constant deny-all predicate (<c>_ =&gt; false</c>).</summary>
    internal static bool IsDenyAll<TEntity>(Expression<Func<TEntity, bool>> filter) {
        return filter.Body is ConstantExpression { Value: false };
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
        var rightBody = new Rebinder(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<TEntity, bool>>(combine(left.Body, rightBody), parameter);
    }

    // One deny-all predicate per entity type; expression trees are immutable, so every caller shares it.
    private static class Deny<TEntity> {
        public static readonly Expression<Func<TEntity, bool>> Filter = _ => false;
    }

    private sealed class DelegateAuthorizer<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter) : IQueryAuthorizer<TEntity>
        where TEntity : class {
        public Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return filter(user, operation);
        }
    }

    private sealed class Rebinder(ParameterExpression from, Expression to) : ExpressionVisitor {
        protected override Expression VisitParameter(ParameterExpression node) {
            return node == from ? to : base.VisitParameter(node);
        }
    }
}
