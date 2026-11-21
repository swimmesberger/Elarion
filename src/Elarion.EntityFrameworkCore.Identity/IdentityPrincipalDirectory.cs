using Elarion.Abstractions.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.EntityFrameworkCore.Identity;

/// <summary>
/// The <see cref="IPrincipalDirectory{TKey}"/> over the ASP.NET Core Identity tables: a principal's roles are its
/// <c>user_roles</c> (by role name), and its claims are its own <c>user_claims</c> plus the <c>role_claims</c> of every
/// role it is in. That is what Identity's default claims principal factory puts on a principal, so the directory agrees
/// with the principal a stock <c>AddElarionIdentity</c> host builds.
/// </summary>
/// <remarks>
/// <para>
/// A host that changes how the principal is built — claims added at sign-in or per request, a role granted outside
/// <c>user_roles</c>, a custom claims factory — must register its own <see cref="IPrincipalDirectory{TKey}"/> that
/// adds the same facts; derive the queries from this type's (<c>Roles.Concat(…)</c>) rather than copying them.
/// </para>
/// <para>
/// A role is listed in <see cref="Roles"/> only; it is not repeated as a claim of the role claim type. Whether an
/// account is locked out or confirmed is not part of it: who is eligible is the caller's candidate set.
/// </para>
/// </remarks>
/// <typeparam name="TDbContext">The context that maps the Identity model.</typeparam>
/// <typeparam name="TRole">The role entity.</typeparam>
/// <typeparam name="TKey">The Identity key type.</typeparam>
public sealed class IdentityPrincipalDirectory<TDbContext, TRole, TKey>(TDbContext dbContext) : IPrincipalDirectory<TKey>
    where TDbContext : DbContext
    where TRole : IdentityRole<TKey>
    where TKey : notnull, IEquatable<TKey> {
    /// <inheritdoc />
    public IQueryable<PrincipalRole<TKey>> Roles =>
        dbContext.Set<IdentityUserRole<TKey>>().Join(
            dbContext.Set<TRole>(),
            membership => membership.RoleId,
            role => role.Id,
            (membership, role) => new PrincipalRole<TKey> { PrincipalId = membership.UserId, Role = role.Name! });

    /// <inheritdoc />
    public IQueryable<PrincipalClaim<TKey>> Claims =>
        dbContext.Set<IdentityUserClaim<TKey>>()
            .Select(claim => new PrincipalClaim<TKey> {
                PrincipalId = claim.UserId, Type = claim.ClaimType!, Value = claim.ClaimValue!
            })
            .Concat(dbContext.Set<IdentityUserRole<TKey>>().Join(
                dbContext.Set<IdentityRoleClaim<TKey>>(),
                membership => membership.RoleId,
                claim => claim.RoleId,
                (membership, claim) => new PrincipalClaim<TKey> {
                    PrincipalId = membership.UserId, Type = claim.ClaimType!, Value = claim.ClaimValue!
                }));
}

/// <summary>Registers the Identity-backed <see cref="IPrincipalDirectory{TKey}"/>.</summary>
public static class IdentityPrincipalDirectoryServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IdentityPrincipalDirectory{TDbContext,TRole,TKey}"/> as the scoped
    /// <see cref="IPrincipalDirectory{TKey}"/>, so a resource rule can list the principals a row is visible to
    /// (<c>WhereAuthorizedFor</c>). A directory the application registered itself is kept (<c>TryAdd</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <typeparam name="TDbContext">The context that maps the Identity model.</typeparam>
    /// <typeparam name="TRole">The role entity.</typeparam>
    /// <typeparam name="TKey">The Identity key type.</typeparam>
    /// <example>
    /// <code>
    /// services.AddElarionIdentityPrincipalDirectory&lt;AppDbContext, ApplicationRole, Guid&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionIdentityPrincipalDirectory<TDbContext, TRole, TKey>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TRole : IdentityRole<TKey>
        where TKey : notnull, IEquatable<TKey> {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPrincipalDirectory<TKey>, IdentityPrincipalDirectory<TDbContext, TRole, TKey>>();
        return services;
    }
}
