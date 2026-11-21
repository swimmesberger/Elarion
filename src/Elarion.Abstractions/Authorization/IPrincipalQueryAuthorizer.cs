using System.Linq.Expressions;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A resource rule that can also be asked in the other direction: as a <b>relation</b> between a row and a principal
/// id that holds exactly when that principal may access the row. <see cref="IQueryAuthorizer{TEntity}"/> binds the
/// caller and leaves a predicate over rows; this leaves both open, so a query can select the principals for a row
/// (<c>WhereAuthorizedFor</c>) from the same rule the list filter applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Complete or refused.</b> An implementation returns the whole rule or throws
/// <see cref="NotSupportedException"/>; it never returns the part it happens to know. A composition
/// (<see cref="QueryAuthorizers.AnyOf{TEntity}"/>, <see cref="QueryAuthorizers.AllOf{TEntity}"/>) is invertible only
/// when every part is, so an answer that covers some alternatives and silently omits others cannot be produced.
/// </para>
/// <para>
/// The generated <c>[ResourceFilter]</c> and every <see cref="QueryAuthorizers"/> building block implement it, except
/// the two that hold an opaque delegate over the live principal: <see cref="QueryAuthorizers.From{TEntity}"/> and
/// <see cref="QueryAuthorizers.When{TEntity}"/>. Facts about a principal that the forward direction reads from
/// <c>ICurrentUser</c> — roles, permission claims — are read from the <see cref="IPrincipalDirectory{TKey}"/> here.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type being authorized.</typeparam>
public interface IPrincipalQueryAuthorizer<TEntity>
    where TEntity : class {
    /// <summary>
    /// The relation <c>(row, principalId) =&gt; …</c> that holds when the principal may access the row for
    /// <paramref name="operation"/>. Never <see langword="null"/>: an unrestricted rule is the constant
    /// <see langword="true"/>, a rule that admits nobody the constant <see langword="false"/>.
    /// </summary>
    /// <param name="directory">Who holds which role and claim.</param>
    /// <param name="operation">The operation asked about.</param>
    /// <typeparam name="TKey">The principal id type of <paramref name="directory"/>.</typeparam>
    /// <exception cref="NotSupportedException">
    /// The rule, or a part of it, cannot be expressed as a relation — or relates rows to another id type than
    /// <typeparamref name="TKey"/>.
    /// </exception>
    Expression<Func<TEntity, TKey, bool>> GetPrincipalRelation<TKey>(
        IPrincipalDirectory<TKey> directory, ResourceOperation operation)
        where TKey : notnull;
}
