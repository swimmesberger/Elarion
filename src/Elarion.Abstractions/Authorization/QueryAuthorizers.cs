using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
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
/// <see cref="ForUser{TEntity,TKey}"/>, the <c>Via</c> rules and the <c>When…</c> and <c>Unless…</c> gates deny a
/// caller who is not authenticated (and, for
/// <see cref="ForUser{TEntity,TKey}"/>, whose id does not parse) before the rule is consulted. Only
/// <see cref="From{TEntity}"/> hands the caller to a delegate unchecked.
/// </para>
/// <para>
/// <b>They read in both directions.</b> Every building block is also an
/// <see cref="IPrincipalQueryAuthorizer{TEntity}"/>, so the rule that filters rows for a caller also lists the
/// principals for a row (<c>WhereAuthorizedFor</c>). <see cref="From{TEntity}"/> and <see cref="When{TEntity}"/> hold an
/// opaque delegate and are the exceptions: a rule that contains one refuses to be inverted rather than answering for
/// the rest.
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
        return new DelegateAuthorizer<TEntity>(filter, "QueryAuthorizers.From");
    }

    /// <summary>The rows matching <paramref name="predicate"/>, for every authenticated caller; nothing for anyone else.</summary>
    /// <remarks>Gate it (<see cref="WhenPermission{TEntity}(string, IQueryAuthorizer{TEntity}, string)"/>) to say who
    /// sees those rows.</remarks>
    public static IQueryAuthorizer<TEntity> Rows<TEntity>(Expression<Func<TEntity, bool>> predicate)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(predicate);
        return new InvertibleAuthorizer<TEntity>(
            (user, _) => user.IsAuthenticated ? predicate : Deny<TEntity>.Filter,
            new RowsRelation<TEntity>(predicate));
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
        return new InvertibleAuthorizer<TEntity>(
            (user, _) => user.IsAuthenticated ? null : Deny<TEntity>.Filter,
            new RowsRelation<TEntity>(null));
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
        return new InvertibleAuthorizer<TEntity>(
            (user, _) => {
                if (!user.IsAuthenticated || !TKey.TryParse(user.UserId, CultureInfo.InvariantCulture, out var userId))
                    return Deny<TEntity>.Filter;

                // A captured local, so a query provider sends the id as a parameter instead of inlining a literal.
                Expression<Func<TKey>> id = () => userId;
                var body = new Rebinder(relation.Parameters[1], id.Body).Visit(relation.Body);
                return Expression.Lambda<Func<TEntity, bool>>(body, relation.Parameters[0]);
            },
            new UserRelation<TEntity, TKey>(relation));
    }

    /// <summary>
    /// Every row, for an authenticated caller whose <see cref="ICurrentUser.UserId"/> parses as
    /// <typeparamref name="TKey"/>; nothing for anyone else. The precondition of a rule whose alternatives assume the
    /// caller has an id of the rows' key type: <c>AllOf(RequireUserId&lt;Contact, Guid&gt;(), AnyOf(…))</c> keeps an
    /// unrestricted alternative (an administrator role) from admitting a caller every <see cref="ForUser{TEntity,TKey}"/>
    /// alternative would deny.
    /// </summary>
    /// <remarks>
    /// Unrestricted for the callers it admits, so it adds nothing to an <see cref="AllOf{TEntity}"/> beyond the check and,
    /// inverted, admits every principal a directory of the same key type lists.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    /// <typeparam name="TKey">The user id type the rows store (<see cref="Guid"/>, <see cref="int"/>, …).</typeparam>
    public static IQueryAuthorizer<TEntity> RequireUserId<TEntity, TKey>()
        where TEntity : class
        where TKey : IParsable<TKey> {
        return new InvertibleAuthorizer<TEntity>(
            (user, _) => user.IsAuthenticated && TKey.TryParse(user.UserId, CultureInfo.InvariantCulture, out TKey? _)
                ? null
                : Deny<TEntity>.Filter,
            new UserRelation<TEntity, TKey>(static (_, _) => true));
    }

    /// <summary>
    /// <paramref name="then"/> for an authenticated caller satisfying <paramref name="condition"/>; nothing for anyone
    /// else. The general gate, for a condition <see cref="WhenPermission{TEntity}(string, IQueryAuthorizer{TEntity}, string)"/>
    /// and <see cref="WhenRole{TEntity}"/> cannot state.
    /// </summary>
    /// <remarks>
    /// The condition is a delegate over the live principal, so a rule gated this way cannot be asked which principals
    /// it admits (<see cref="IPrincipalQueryAuthorizer{TEntity}"/>). Prefer the permission and role gates, which can.
    /// </remarks>
    public static IQueryAuthorizer<TEntity> When<TEntity>(
        Func<ICurrentUser, bool> condition, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(then);
        return new DelegateAuthorizer<TEntity>(Gate(condition, then), "QueryAuthorizers.When");
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
        ArgumentNullException.ThrowIfNull(then);
        return new InvertibleAuthorizer<TEntity>(
            Gate(user => user.HasClaim(claimType, permission), then),
            new ClaimGateRelation<TEntity>(claimType, permission, then));
    }

    /// <summary>
    /// <paramref name="then"/> for an authenticated caller who holds <b>no</b> claim of <paramref name="claimType"/>;
    /// nothing for one who does — so a session marked by a claim drops an alternative: while an administrator views the
    /// application as another user (an <c>act</c> claim on the session), the administrator's bypass does not apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A negated gate grants when the claim is <b>missing</b>. Use it to withdraw an alternative from sessions that
    /// carry a claim the host adds deliberately, never to deny what a claim the host might fail to add would deny.
    /// </para>
    /// <para>
    /// The inverse reads the claim from the principal directory. A session claim such as an impersonation marker is not
    /// a fact of the principal, so the directory lists it for nobody and "who may access this row" answers for ordinary
    /// sessions, which is the question it asks.
    /// </para>
    /// </remarks>
    /// <param name="claimType">The claim type whose presence, with any value, withdraws <paramref name="then"/>.</param>
    /// <param name="then">What a caller without the claim may access.</param>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    /// <example>
    /// <code>
    /// QueryAuthorizers.AnyOf(
    ///     QueryAuthorizers.UnlessClaim("act", QueryAuthorizers.WhenRole("Admin", QueryAuthorizers.Unrestricted&lt;Contact&gt;())),
    ///     ContactOwnerFilter.Specification)
    /// </code>
    /// </example>
    public static IQueryAuthorizer<TEntity> UnlessClaim<TEntity>(string claimType, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentException.ThrowIfNullOrEmpty(claimType);
        ArgumentNullException.ThrowIfNull(then);
        return new InvertibleAuthorizer<TEntity>(
            Gate(user => !user.GetClaimValues(claimType).Any(), then),
            new NegatedClaimGateRelation<TEntity>(claimType, null, then));
    }

    /// <summary>
    /// <paramref name="then"/> for an authenticated caller who does <b>not</b> hold the claim <paramref name="claimType"/>
    /// with <paramref name="value"/>; nothing for one who does. See
    /// <see cref="UnlessClaim{TEntity}(string, IQueryAuthorizer{TEntity})"/> for when a negated gate fits.
    /// </summary>
    /// <param name="claimType">The claim type.</param>
    /// <param name="value">The claim value whose presence withdraws <paramref name="then"/>.</param>
    /// <param name="then">What a caller without the claim may access.</param>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    public static IQueryAuthorizer<TEntity> UnlessClaim<TEntity>(
        string claimType, string value, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentException.ThrowIfNullOrEmpty(claimType);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(then);
        return new InvertibleAuthorizer<TEntity>(
            Gate(user => !user.HasClaim(claimType, value), then),
            new NegatedClaimGateRelation<TEntity>(claimType, value, then));
    }

    /// <summary><paramref name="then"/> for callers in <paramref name="role"/>; nothing for anyone else.</summary>
    public static IQueryAuthorizer<TEntity> WhenRole<TEntity>(string role, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(then);
        return new InvertibleAuthorizer<TEntity>(
            Gate(user => user.IsInRole(role), then),
            new RoleGateRelation<TEntity>(role, then));
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

        return new InvertibleAuthorizer<TEntity>(
            (user, operation) =>
                Array.IndexOf(allowed, operation) >= 0 ? authorizer.GetFilter(user, operation) : Deny<TEntity>.Filter,
            new OperationsRelation<TEntity>(allowed, authorizer));
    }

    /// <summary>
    /// The rows with <b>at least one owner</b> the caller may access under <paramref name="owner"/> — "an e-mail is
    /// readable when it is assigned to a record the caller may read" — with the owners reached from the row: a
    /// collection navigation (<c>e =&gt; e.Contacts</c>) or a join through a link table
    /// (<c>e =&gt; e.Links.Select(link =&gt; link.Contact)</c>). Denies a caller who is not authenticated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's rule is used as it stands, composed or generated, so a change to it reaches every type that is
    /// visible through it. Both directions read it: the filter is
    /// <c>row =&gt; owners(row).Any(o =&gt; ownerFilter(o))</c>, and the relation the inverse reads is
    /// <c>(row, principal) =&gt; owners(row).Any(o =&gt; ownerRelation(o, principal))</c>.
    /// </para>
    /// <para>
    /// A row needs an owner: a row with none is not visible through this rule, also to a caller for whom the owner's
    /// rule is unrestricted. Unlike <c>WhereAuthorizedVia</c>, which keeps every row for such a caller, this states
    /// "assigned to a record the caller may access"; give unassigned rows an alternative of their own.
    /// </para>
    /// <para>
    /// For owners that have no navigation — a <c>(kind, id)</c> pointer — use
    /// <see cref="Via{TEntity,TOwner}(IQueryable{TOwner}, Expression{Func{TEntity,TOwner,bool}}, IQueryAuthorizer{TOwner}, ResourceOperation?)"/>.
    /// </para>
    /// </remarks>
    /// <param name="owners">The row's owners, as an expression the query provider translates.</param>
    /// <param name="owner">The owner type's rule.</param>
    /// <param name="ownerOperation">
    /// The operation asked of the owner. Defaults to the operation asked of the row; pass
    /// <see cref="ResourceOperation.Read"/> for "whoever may read the record may change its notes".
    /// </param>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    /// <typeparam name="TOwner">The owner type.</typeparam>
    /// <example>
    /// <code>
    /// // An e-mail is visible to whoever may read a contact it is assigned to.
    /// QueryAuthorizers.Via&lt;Email, Contact&gt;(e =&gt; e.Links.Select(link =&gt; link.Contact), ContactVisibility.Authorizer)
    /// </code>
    /// </example>
    public static IQueryAuthorizer<TEntity> Via<TEntity, TOwner>(
        Expression<Func<TEntity, IEnumerable<TOwner>>> owners,
        IQueryAuthorizer<TOwner> owner,
        ResourceOperation? ownerOperation = null)
        where TEntity : class
        where TOwner : class {
        ArgumentNullException.ThrowIfNull(owners);
        return Via(new NavigationOwners<TEntity, TOwner>(owners), owner, ownerOperation);
    }

    /// <summary>
    /// The rows with <b>at least one owner</b> in <paramref name="owners"/> that <paramref name="joins"/> relates to the
    /// row and the caller may access under <paramref name="owner"/> — for owners the row points at without a
    /// navigation, such as a <c>(kind, id)</c> pointer shared by several owner types. Denies a caller who is not
    /// authenticated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter is <c>row =&gt; owners.Any(o =&gt; joins(row, o) &amp;&amp; ownerFilter(o))</c>, one <c>EXISTS</c> in the same
    /// statement; the relation the inverse reads is the same with the owner's relation in place of its filter. A row
    /// with no matching owner is not visible through this rule (see
    /// <see cref="Via{TEntity,TOwner}(Expression{Func{TEntity,IEnumerable{TOwner}}}, IQueryAuthorizer{TOwner}, ResourceOperation?)"/>).
    /// </para>
    /// <para>
    /// <b>Narrow a shared pointer to the owner's kind in <paramref name="joins"/></b>: without it, a row pointing at an
    /// invoice whose id equals a visible contact's id passes.
    /// </para>
    /// <para>
    /// <paramref name="owners"/> becomes part of every query the rule filters, so it must belong to the store those
    /// queries run against. For a <c>DbContext</c> set, compose the rule where the context lives — in a scoped
    /// registration (<c>services.AddScoped&lt;IQueryAuthorizer&lt;Email&gt;&gt;(sp =&gt; …)</c>), not in a static field.
    /// </para>
    /// </remarks>
    /// <param name="owners">The owners' set (<c>db.Contacts</c>).</param>
    /// <param name="joins">Whether an owner is one of the row's owners.</param>
    /// <param name="owner">The owner type's rule.</param>
    /// <param name="ownerOperation">The operation asked of the owner; defaults to the operation asked of the row.</param>
    /// <typeparam name="TEntity">The entity type being authorized.</typeparam>
    /// <typeparam name="TOwner">The owner type.</typeparam>
    /// <example>
    /// <code>
    /// // An e-mail is visible to whoever may read a contact one of its links points at.
    /// QueryAuthorizers.Via&lt;Email, Contact&gt;(
    ///     db.Contacts,
    ///     (e, contact) =&gt; e.Links.Any(link =&gt; link.Kind == RecordKind.Contact &amp;&amp; link.RecordId == contact.Id),
    ///     ContactVisibility.Authorizer)
    /// </code>
    /// </example>
    public static IQueryAuthorizer<TEntity> Via<TEntity, TOwner>(
        IQueryable<TOwner> owners,
        Expression<Func<TEntity, TOwner, bool>> joins,
        IQueryAuthorizer<TOwner> owner,
        ResourceOperation? ownerOperation = null)
        where TEntity : class
        where TOwner : class {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(joins);
        return Via(new JoinedOwners<TEntity, TOwner>(owners, joins), owner, ownerOperation);
    }

    /// <summary>A row is visible when <b>any</b> authorizer makes it visible (OR); one unrestricted authorizer lifts the restriction.</summary>
    /// <exception cref="ArgumentException">An authorizer is <see langword="null"/>.</exception>
    public static IQueryAuthorizer<TEntity> AnyOf<TEntity>(params IQueryAuthorizer<TEntity>[] authorizers)
        where TEntity : class {
        var snapshot = Snapshot(authorizers);
        return new InvertibleAuthorizer<TEntity>(
            (user, operation) => {
                Expression<Func<TEntity, bool>>? combined = null;
                foreach (var authorizer in snapshot) {
                    var filter = authorizer.GetFilter(user, operation);
                    if (filter is null) return null;
                    // An alternative that denies everyone adds nothing; leaving it out keeps the SQL to the live rules.
                    if (IsDenyAll(filter)) continue;

                    combined = combined is null ? filter : Combine(combined, filter, Expression.OrElse);
                }

                return combined ?? Deny<TEntity>.Filter;
            },
            new CombinedRelation<TEntity>(snapshot, any: true));
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
        return new InvertibleAuthorizer<TEntity>(
            (user, operation) => {
                Expression<Func<TEntity, bool>>? combined = null;
                foreach (var authorizer in snapshot) {
                    if (authorizer.GetFilter(user, operation) is not { } filter) continue;
                    if (IsDenyAll(filter)) return filter;

                    combined = combined is null ? filter : Combine(combined, filter, Expression.AndAlso);
                }

                return combined;
            },
            new CombinedRelation<TEntity>(snapshot, any: false));
    }

    /// <summary>
    /// The relation <c>(row, principalId) =&gt; …</c> of <paramref name="authorizer"/> — its rule with the principal
    /// left open — or a refusal naming the part that has none.
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="authorizer"/>, or a part of it, is not an
    /// <see cref="IPrincipalQueryAuthorizer{TEntity}"/>.</exception>
    internal static Expression<Func<TEntity, TKey, bool>> RelationOf<TEntity, TKey>(
        IQueryAuthorizer<TEntity> authorizer, IPrincipalDirectory<TKey> directory, ResourceOperation operation)
        where TEntity : class
        where TKey : notnull {
        if (authorizer is IPrincipalQueryAuthorizer<TEntity> invertible)
            return invertible.GetPrincipalRelation(directory, operation);

        var name = authorizer is DelegateAuthorizer<TEntity> opaque ? opaque.Description : authorizer.GetType().Name;
        throw new NotSupportedException(
            $"'{name}' is not an IPrincipalQueryAuthorizer<{typeof(TEntity).Name}>, so the principals this rule admits "
            + "cannot be listed completely. State the alternative with ForUser, WhenPermission, WhenRole, Rows or a "
            + "[ResourceFilter], or implement IPrincipalQueryAuthorizer on it.");
    }

    /// <summary>Whether <paramref name="filter"/> is the constant deny-all predicate (<c>_ =&gt; false</c>).</summary>
    internal static bool IsDenyAll<TEntity>(Expression<Func<TEntity, bool>> filter) {
        return filter.Body is ConstantExpression { Value: false };
    }

    private static IQueryAuthorizer<TEntity> Via<TEntity, TOwner>(
        OwnerSource<TEntity, TOwner> owners, IQueryAuthorizer<TOwner> owner, ResourceOperation? ownerOperation)
        where TEntity : class
        where TOwner : class {
        ArgumentNullException.ThrowIfNull(owner);
        if (ownerOperation is { Name: null })
            throw new ArgumentException("The owner operation is the invalid default value.", nameof(ownerOperation));

        return new InvertibleAuthorizer<TEntity>(
            (user, operation) => {
                if (!user.IsAuthenticated) return Deny<TEntity>.Filter;

                var filter = owner.GetFilter(user, ownerOperation ?? operation);
                if (filter is not null && IsDenyAll(filter)) return Deny<TEntity>.Filter;

                var row = Expression.Parameter(typeof(TEntity), "row");
                var candidate = Expression.Parameter(typeof(TOwner), "owner");
                var condition = filter is null
                    ? null
                    : new Rebinder(filter.Parameters[0], candidate).Visit(filter.Body);
                return Expression.Lambda<Func<TEntity, bool>>(owners.Exists(row, candidate, condition), row);
            },
            new ViaRelation<TEntity, TOwner>(owners, owner, ownerOperation));
    }

    private static Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> Gate<TEntity>(
        Func<ICurrentUser, bool> condition, IQueryAuthorizer<TEntity> then)
        where TEntity : class {
        return (user, operation) =>
            user.IsAuthenticated && condition(user) ? then.GetFilter(user, operation) : Deny<TEntity>.Filter;
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

    // An authorizer whose rule is an opaque delegate over the live principal: it has no relation to offer.
    private sealed class DelegateAuthorizer<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter,
        string description) : IQueryAuthorizer<TEntity>
        where TEntity : class {
        public string Description => description;

        public Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return filter(user, operation);
        }
    }

    // A building block: the filter for a bound caller, and the same rule as a relation with the principal left open.
    private sealed class InvertibleAuthorizer<TEntity>(
        Func<ICurrentUser, ResourceOperation, Expression<Func<TEntity, bool>>?> filter,
        Relation<TEntity> relation) : IQueryAuthorizer<TEntity>, IPrincipalQueryAuthorizer<TEntity>
        where TEntity : class {
        public Expression<Func<TEntity, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) {
            return filter(user, operation);
        }

        public Expression<Func<TEntity, TKey, bool>> GetPrincipalRelation<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation)
            where TKey : notnull {
            ArgumentNullException.ThrowIfNull(directory);
            return relation.Build(directory, operation);
        }
    }

    // The principal id type is only known when the relation is asked for, hence a generic method rather than a delegate.
    private abstract class Relation<TEntity>
        where TEntity : class {
        public abstract Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation)
            where TKey : notnull;

        protected static Expression<Func<TEntity, TKey, bool>> Constant<TKey>(bool value) {
            return value ? static (_, _) => true : static (_, _) => false;
        }

        protected static bool IsConstant<TKey>(Expression<Func<TEntity, TKey, bool>> relation, bool value) {
            return relation.Body is ConstantExpression constant && Equals(constant.Value, value);
        }

        // A predicate over the principal alone (membership of a holder set, or its absence), widened to a relation and
        // AND-ed with what the gate lets the principals it admits access.
        protected static Expression<Func<TEntity, TKey, bool>> Gated<TKey>(
            IQueryable<TKey> holders, Expression<Func<TEntity, TKey, bool>> then, bool negate = false) {
            if (IsConstant(then, false)) return then;

            Expression<Func<TKey, bool>> holds = principal => holders.Contains(principal);
            var principalParameter = then.Parameters[1];
            var holdsBody = new Rebinder(holds.Parameters[0], principalParameter).Visit(holds.Body);
            if (negate) holdsBody = Expression.Not(holdsBody);
            var body = IsConstant(then, true) ? holdsBody : Expression.AndAlso(holdsBody, then.Body);
            return Expression.Lambda<Func<TEntity, TKey, bool>>(body, then.Parameters);
        }
    }

    // Rows(predicate): every principal, for the matching rows. A null predicate is Unrestricted.
    private sealed class RowsRelation<TEntity>(Expression<Func<TEntity, bool>>? predicate) : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            return predicate is null
                ? Constant<TKey>(true)
                : Expression.Lambda<Func<TEntity, TKey, bool>>(
                    predicate.Body, predicate.Parameters[0], Expression.Parameter(typeof(TKey), "principal"));
        }
    }

    // ForUser: the declared relation itself, when the directory speaks of the same id type.
    private sealed class UserRelation<TEntity, TUserKey>(Expression<Func<TEntity, TUserKey, bool>> relation)
        : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            return relation as Expression<Func<TEntity, TKey, bool>>
                   ?? throw new NotSupportedException(
                       $"A ForUser rule relates {typeof(TEntity).Name} rows to {typeof(TUserKey).Name} ids, but the "
                       + $"principal directory lists {typeof(TKey).Name} ids.");
        }
    }

    private sealed class ClaimGateRelation<TEntity>(string claimType, string value, IQueryAuthorizer<TEntity> then)
        : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            var holders = directory.Claims
                .Where(claim => claim.Type == claimType && claim.Value == value)
                .Select(claim => claim.PrincipalId);
            return Gated(holders, RelationOf(then, directory, operation));
        }
    }

    // UnlessClaim: the principals without the claim (any value when value is null), AND-ed with what then grants.
    private sealed class NegatedClaimGateRelation<TEntity>(string claimType, string? value, IQueryAuthorizer<TEntity> then)
        : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            var claims = directory.Claims.Where(claim => claim.Type == claimType);
            if (value is not null) claims = claims.Where(claim => claim.Value == value);
            return Gated(claims.Select(claim => claim.PrincipalId), RelationOf(then, directory, operation), negate: true);
        }
    }

    private sealed class RoleGateRelation<TEntity>(string role, IQueryAuthorizer<TEntity> then) : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            var members = directory.Roles.Where(membership => membership.Role == role)
                .Select(membership => membership.PrincipalId);
            return Gated(members, RelationOf(then, directory, operation));
        }
    }

    private sealed class OperationsRelation<TEntity>(ResourceOperation[] allowed, IQueryAuthorizer<TEntity> authorizer)
        : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            return Array.IndexOf(allowed, operation) >= 0
                ? RelationOf(authorizer, directory, operation)
                : Constant<TKey>(false);
        }
    }

    // Via: the owner's relation, asked of the row's owners — (row, principal) => owners(row).Any(o => relation(o, principal)).
    private sealed class ViaRelation<TEntity, TOwner>(
        OwnerSource<TEntity, TOwner> owners, IQueryAuthorizer<TOwner> owner, ResourceOperation? ownerOperation)
        : Relation<TEntity>
        where TEntity : class
        where TOwner : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            var relation = RelationOf(owner, directory, ownerOperation ?? operation);
            if (IsConstantBody(relation, false)) return Constant<TKey>(false);

            // Fresh parameters: the owner's relation may reuse parameter instances of its own parts.
            var row = Expression.Parameter(typeof(TEntity), "row");
            var principal = Expression.Parameter(typeof(TKey), "principal");
            var candidate = Expression.Parameter(typeof(TOwner), "owner");
            Expression? condition = null;
            if (!IsConstantBody(relation, true)) {
                condition = new Rebinder(relation.Parameters[0], candidate).Visit(relation.Body);
                condition = new Rebinder(relation.Parameters[1], principal).Visit(condition);
            }

            return Expression.Lambda<Func<TEntity, TKey, bool>>(owners.Exists(row, candidate, condition), row, principal);
        }
    }

    // Where a Via rule finds a row's owners: "some owner of the row satisfies the condition" as an expression over the row.
    private abstract class OwnerSource<TEntity, TOwner> {
        /// <summary>
        /// "Some owner of <paramref name="row"/> satisfies <paramref name="condition"/>", the condition an expression over
        /// <paramref name="owner"/>; a <see langword="null"/> condition asks only whether the row has an owner.
        /// </summary>
        public abstract Expression Exists(ParameterExpression row, ParameterExpression owner, Expression? condition);
    }

    // The owners are a collection the row reaches: owners(row).Any(o => condition).
    private sealed class NavigationOwners<TEntity, TOwner>(Expression<Func<TEntity, IEnumerable<TOwner>>> owners)
        : OwnerSource<TEntity, TOwner> {
        // Compiler-built templates close the generic Enumerable.Any at compile time (no MakeGenericMethod), AOT-safe.
        private static readonly MethodInfo AnyMethod = CalledMethod<Func<IEnumerable<TOwner>, bool>>(static s => s.Any());

        private static readonly MethodInfo AnyWhereMethod =
            CalledMethod<Func<IEnumerable<TOwner>, Func<TOwner, bool>, bool>>(static (s, predicate) => s.Any(predicate));

        public override Expression Exists(ParameterExpression row, ParameterExpression owner, Expression? condition) {
            var source = new Rebinder(owners.Parameters[0], row).Visit(owners.Body);
            return condition is null
                ? Expression.Call(AnyMethod, source)
                : Expression.Call(AnyWhereMethod, source, Expression.Lambda<Func<TOwner, bool>>(condition, owner));
        }
    }

    // The owners are a set the row joins: owners.Any(o => joins(row, o) && condition).
    private sealed class JoinedOwners<TEntity, TOwner>(
        IQueryable<TOwner> owners, Expression<Func<TEntity, TOwner, bool>> joins) : OwnerSource<TEntity, TOwner> {
        private static readonly MethodInfo AnyWhereMethod =
            CalledMethod<Func<IQueryable<TOwner>, Expression<Func<TOwner, bool>>, bool>>(
                static (s, predicate) => s.Any(predicate));

        public override Expression Exists(ParameterExpression row, ParameterExpression owner, Expression? condition) {
            var joined = new Rebinder(joins.Parameters[0], row).Visit(joins.Body);
            joined = new Rebinder(joins.Parameters[1], owner).Visit(joined);
            var body = condition is null ? joined : Expression.AndAlso(joined, condition);
            return Expression.Call(
                AnyWhereMethod, owners.Expression, Expression.Quote(Expression.Lambda<Func<TOwner, bool>>(body, owner)));
        }
    }

    private static bool IsConstantBody(LambdaExpression lambda, bool value) {
        return lambda.Body is ConstantExpression { Value: bool constant } && constant == value;
    }

    private static MethodInfo CalledMethod<TDelegate>(Expression<TDelegate> template) {
        return ((MethodCallExpression)template.Body).Method;
    }

    // AnyOf / AllOf: the parts' relations OR-ed / AND-ed over one pair of parameters. Every part is asked, so a part
    // without a relation refuses the whole composition instead of being left out of it.
    private sealed class CombinedRelation<TEntity>(IQueryAuthorizer<TEntity>[] parts, bool any) : Relation<TEntity>
        where TEntity : class {
        public override Expression<Func<TEntity, TKey, bool>> Build<TKey>(
            IPrincipalDirectory<TKey> directory, ResourceOperation operation) {
            var relations = new Expression<Func<TEntity, TKey, bool>>[parts.Length];
            for (var i = 0; i < parts.Length; i++) relations[i] = RelationOf(parts[i], directory, operation);

            Expression<Func<TEntity, TKey, bool>>? combined = null;
            foreach (var relation in relations) {
                // true decides an OR and false an AND; the other constant is neutral.
                if (IsConstant(relation, any)) return relation;
                if (IsConstant(relation, !any)) continue;
                if (combined is null) {
                    combined = relation;
                    continue;
                }

                var body = new Rebinder(relation.Parameters[0], combined.Parameters[0]).Visit(relation.Body);
                body = new Rebinder(relation.Parameters[1], combined.Parameters[1]).Visit(body);
                combined = Expression.Lambda<Func<TEntity, TKey, bool>>(
                    any ? Expression.OrElse(combined.Body, body) : Expression.AndAlso(combined.Body, body),
                    combined.Parameters);
            }

            // Nothing left: an OR of alternatives that admit nobody, or an AND of parts that restrict nothing.
            return combined ?? Constant<TKey>(!any);
        }
    }

    private sealed class Rebinder(ParameterExpression from, Expression to) : ExpressionVisitor {
        protected override Expression VisitParameter(ParameterExpression node) {
            return node == from ? to : base.VisitParameter(node);
        }
    }
}
