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
    /// Restricts <paramref name="source"/> to the rows <paramref name="user"/> may <b>not</b> access for
    /// <paramref name="operation"/> (defaulting to <see cref="ResourceOperation.Read"/>) — the exact complement of
    /// <see cref="WhereAuthorized{TEntity}"/>, for callers that subtract hidden rows (<c>WHERE owner_id NOT IN (hidden
    /// ids)</c>) instead of selecting visible ones. An unrestricted caller hides nothing, so the result is empty; a
    /// caller denied everything gets <paramref name="source"/> unchanged.
    /// </summary>
    /// <remarks>
    /// The two sets partition <paramref name="source"/> wherever a predicate is either true or false for every row:
    /// in memory, and under Entity Framework Core's default C# null semantics. With <c>UseRelationalNulls</c> a
    /// predicate that evaluates to SQL <c>NULL</c> puts its row in neither set.
    /// </remarks>
    /// <example>
    /// <code>
    /// // The contacts hidden from the caller, as a subquery other tables subtract.
    /// var hiddenContactIds = db.Contacts.WhereNotAuthorized(contactAccess, currentUser).Select(c =&gt; c.Id);
    /// var files = db.Files.Where(f =&gt; f.ContactId == null || !hiddenContactIds.Contains(f.ContactId.Value));
    /// </code>
    /// </example>
    public static IQueryable<TEntity> WhereNotAuthorized<TEntity>(
        this IQueryable<TEntity> source,
        IQueryAuthorizer<TEntity> authorizer,
        ICurrentUser user,
        ResourceOperation? operation = null)
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(user);

        var filter = authorizer.GetFilter(user, operation ?? ResourceOperation.Read);
        if (filter is null) return source.Where(static _ => false);
        if (QueryAuthorizers.IsDenyAll(filter)) return source;

        return source.Where(Expression.Lambda<Func<TEntity, bool>>(Expression.Not(filter.Body), filter.Parameters));
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

    /// <summary>
    /// Restricts <paramref name="principals"/> to those who may access <b>at least one</b> of <paramref name="rows"/>
    /// for <paramref name="operation"/> (defaulting to <see cref="ResourceOperation.Read"/>) — the inverse of
    /// <see cref="WhereAuthorized{TEntity}"/>: "who may see this record" for mention candidates, notification
    /// recipients or an access overview, answered by the rule the list filter applies. Pass one row
    /// (<c>db.Contacts.Where(c =&gt; c.Id == id)</c>) to ask about one record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The candidates are the caller's.</b> The result is a subset of <paramref name="principals"/>, so the caller
    /// decides who is eligible at all — the accounts that can still sign in, the staff of one tenant — and the rule
    /// decides who among them may access the rows. A principal the rule would admit but the candidates leave out is not
    /// returned, and no principal is returned that the rule would deny.
    /// </para>
    /// <para>
    /// <b>Complete or refused.</b> Every part of the rule must be an <see cref="IPrincipalQueryAuthorizer{TEntity}"/>;
    /// otherwise the call throws instead of answering for the parts that are. Roles and claims are read from
    /// <paramref name="directory"/>, whose contract is agreement with the live principal (see
    /// <see cref="IPrincipalDirectory{TKey}"/>).
    /// </para>
    /// <para>
    /// <paramref name="rows"/> is used as given: its query filters decide which rows exist for this question, exactly
    /// as they do for <see cref="WhereAuthorized{TEntity}"/>. The whole answer is one statement.
    /// </para>
    /// </remarks>
    /// <param name="principals">The candidate principal ids.</param>
    /// <param name="rows">The rows asked about, typically one.</param>
    /// <param name="authorizer">The entity's rule.</param>
    /// <param name="directory">Who holds which role and claim.</param>
    /// <param name="operation">The operation; defaults to <see cref="ResourceOperation.Read"/>.</param>
    /// <typeparam name="TKey">The principal id type.</typeparam>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <exception cref="NotSupportedException">The rule, or a part of it, cannot be asked which principals it admits.</exception>
    /// <example>
    /// <code>
    /// // The active staff accounts that may read this contact.
    /// var readers = users.ActiveStaff(now)
    ///     .WhereAuthorizedFor(db.Contacts.Where(c =&gt; c.Id == contactId), contactAccess, directory);
    /// </code>
    /// </example>
    public static IQueryable<TKey> WhereAuthorizedFor<TKey, TEntity>(
        this IQueryable<TKey> principals,
        IQueryable<TEntity> rows,
        IQueryAuthorizer<TEntity> authorizer,
        IPrincipalDirectory<TKey> directory,
        ResourceOperation? operation = null)
        where TKey : notnull
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(directory);

        var relation = QueryAuthorizers.RelationOf(authorizer, directory, operation ?? ResourceOperation.Read);

        // principal => rows.Any(row => relation(row, principal)). The Any call comes from a compiler-built template, so
        // the generic method is closed at compile time (no MakeGenericMethod).
        Expression<Func<IQueryable<TEntity>, Expression<Func<TEntity, bool>>, bool>> template =
            static (candidates, predicate) => candidates.Any(predicate);
        var any = ((MethodCallExpression)template.Body).Method;
        var admits = Expression.Lambda<Func<TEntity, bool>>(relation.Body, relation.Parameters[0]);
        var body = Expression.Call(any, rows.Expression, Expression.Quote(admits));
        return principals.Where(Expression.Lambda<Func<TKey, bool>>(body, relation.Parameters[1]));
    }

    /// <summary>
    /// Restricts <paramref name="principals"/> to those who may access <b>every one</b> of <paramref name="rows"/> for
    /// <paramref name="operation"/> (defaulting to <see cref="ResourceOperation.Read"/>) — the inverse for a selection
    /// action: "whom may these records be handed to", where each candidate must be able to see all of them. Nobody is
    /// returned for no rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same rule and contract as <see cref="WhereAuthorizedFor{TKey,TEntity}"/>: the result is a subset of
    /// <paramref name="principals"/>, every part of the rule must be an <see cref="IPrincipalQueryAuthorizer{TEntity}"/>
    /// (or the call throws), roles and claims come from <paramref name="directory"/>, and the whole answer is one
    /// statement: <c>WHERE EXISTS (rows) AND NOT EXISTS (rows the principal may not access)</c>.
    /// </para>
    /// <para>
    /// <paramref name="rows"/> is used as given, so a row its query filters hide is not asked about. Pass exactly the
    /// rows of the selection; a selected id that names no row (deleted meanwhile) leaves the others to decide.
    /// </para>
    /// <para>
    /// The "every" is exact wherever the rule's predicate is either true or false for every row: in memory, and under
    /// Entity Framework Core's default C# null semantics. With <c>UseRelationalNulls</c> a relation that evaluates to SQL
    /// <c>NULL</c> for a row does not exclude the principal. The forward filter stays the authority.
    /// </para>
    /// </remarks>
    /// <param name="principals">The candidate principal ids.</param>
    /// <param name="rows">The rows asked about.</param>
    /// <param name="authorizer">The entity's rule.</param>
    /// <param name="directory">Who holds which role and claim.</param>
    /// <param name="operation">The operation; defaults to <see cref="ResourceOperation.Read"/>.</param>
    /// <typeparam name="TKey">The principal id type.</typeparam>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <exception cref="NotSupportedException">The rule, or a part of it, cannot be asked which principals it admits.</exception>
    /// <example>
    /// <code>
    /// // The staff accounts the selected e-mails may be put in charge of: each must be able to read all of them.
    /// var assignees = await users.ActiveStaff(now)
    ///     .WhereAuthorizedForAll(db.Emails.Where(e =&gt; selectedIds.Contains(e.Id)), emailAccess, directory)
    ///     .ToListAsync(ct);
    /// </code>
    /// </example>
    public static IQueryable<TKey> WhereAuthorizedForAll<TKey, TEntity>(
        this IQueryable<TKey> principals,
        IQueryable<TEntity> rows,
        IQueryAuthorizer<TEntity> authorizer,
        IPrincipalDirectory<TKey> directory,
        ResourceOperation? operation = null)
        where TKey : notnull
        where TEntity : class {
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(directory);

        var relation = QueryAuthorizers.RelationOf(authorizer, directory, operation ?? ResourceOperation.Read);

        // principal => rows.Any() && rows.All(row => relation(row, principal)). "Every row of none" would admit every
        // candidate; an empty selection admits nobody instead, as WhereAuthorizedFor does. Templates as above.
        Expression<Func<IQueryable<TEntity>, bool>> anyTemplate = static candidates => candidates.Any();
        Expression<Func<IQueryable<TEntity>, Expression<Func<TEntity, bool>>, bool>> allTemplate =
            static (candidates, predicate) => candidates.All(predicate);
        var any = ((MethodCallExpression)anyTemplate.Body).Method;
        var all = ((MethodCallExpression)allTemplate.Body).Method;
        var admits = Expression.Lambda<Func<TEntity, bool>>(relation.Body, relation.Parameters[0]);
        var body = Expression.AndAlso(
            Expression.Call(any, rows.Expression),
            Expression.Call(all, rows.Expression, Expression.Quote(admits)));
        return principals.Where(Expression.Lambda<Func<TKey, bool>>(body, relation.Parameters[1]));
    }

    private sealed class Substitution(Dictionary<ParameterExpression, Expression> replacements) : ExpressionVisitor {
        protected override Expression VisitParameter(ParameterExpression node) {
            return replacements.TryGetValue(node, out var replacement) ? replacement : base.VisitParameter(node);
        }
    }
}
