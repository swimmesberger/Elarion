namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The stored side of who a principal is: which roles and which claims each principal holds, as queries. It is what
/// lets a resource rule be asked in the other direction — not "which rows may this caller access" but "which
/// principals may access this row" (<c>WhereAuthorizedFor</c>) — where a gate such as "holds <c>contacts.read</c>" cannot
/// be read off a live <c>ICurrentUser</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract is agreement with the live principal.</b> For every principal that can sign in,
/// <see cref="Roles"/> must hold exactly the roles its <c>ICurrentUser.IsInRole</c> answers for and
/// <see cref="Claims"/> exactly the claims its <c>ICurrentUser.HasClaim</c> answers for — <b>effective</b> claims, so
/// the claims a role carries are listed for each of its members. A host that adds roles or claims while it builds the
/// principal (an instance owner who is made an administrator on every request) must add them here too; only the
/// application knows how its principals are built, so only it can keep the two in step. Pin the agreement with a test
/// that builds a principal and compares it with these queries.
/// </para>
/// <para>
/// Both members return composable queries over the store the consumer queries, so they join into the consumer's
/// statement and are never materialized here. <c>Elarion.EntityFrameworkCore.Identity</c> ships the directory over the
/// ASP.NET Core Identity tables, which mirrors Identity's default claims principal.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The principal id type, as the rows store it (an owner column, a membership table).</typeparam>
public interface IPrincipalDirectory<TKey>
    where TKey : notnull {
    /// <summary>One element per principal and role it is in. Role names are compared ordinally, as <c>IsInRole</c> does.</summary>
    IQueryable<PrincipalRole<TKey>> Roles { get; }

    /// <summary>One element per principal and claim it holds, including the claims of its roles.</summary>
    IQueryable<PrincipalClaim<TKey>> Claims { get; }
}

/// <summary>A principal's membership in a role, as <see cref="IPrincipalDirectory{TKey}.Roles"/> lists it.</summary>
/// <typeparam name="TKey">The principal id type.</typeparam>
public sealed record PrincipalRole<TKey>
    where TKey : notnull {
    /// <summary>The principal.</summary>
    public required TKey PrincipalId { get; init; }

    /// <summary>The role name.</summary>
    public required string Role { get; init; }
}

/// <summary>A claim a principal holds, as <see cref="IPrincipalDirectory{TKey}.Claims"/> lists it.</summary>
/// <typeparam name="TKey">The principal id type.</typeparam>
public sealed record PrincipalClaim<TKey>
    where TKey : notnull {
    /// <summary>The principal.</summary>
    public required TKey PrincipalId { get; init; }

    /// <summary>The claim type (<c>"permission"</c>).</summary>
    public required string Type { get; init; }

    /// <summary>The claim value (<c>"contacts.read"</c>).</summary>
    public required string Value { get; init; }
}
