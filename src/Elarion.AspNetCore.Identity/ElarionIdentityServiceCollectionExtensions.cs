using System.Security.Claims;
using Elarion.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Wires ASP.NET Core Identity for an Elarion host: <c>AddIdentity</c> + EF stores against the application's
/// plain <c>DbContext</c>, the Identity-flavored <c>ICurrentUser</c> claim mapping, and the transport-neutral
/// authorization runtime. Cookie/authentication policy stays the host's responsibility.
/// </summary>
public static class ElarionIdentityServiceCollectionExtensions {
    /// <summary>
    /// Registers ASP.NET Core Identity (<typeparamref name="TUser"/>/<typeparamref name="TRole"/>) backed by
    /// <typeparamref name="TDbContext"/>, maps <see cref="Elarion.Abstractions.Identity.ICurrentUser"/> to the
    /// Identity claim types, and registers the Elarion authorizer so handler <c>[Require*]</c> attributes are
    /// enforced against the signed-in principal.
    /// </summary>
    public static IdentityBuilder AddElarionIdentity<TUser, TRole, TKey, TDbContext>(
        this IServiceCollection services,
        Action<IdentityOptions>? configureIdentity = null,
        Action<ElarionIdentityOptions>? configure = null)
        where TUser : class
        where TRole : class
        where TKey : IEquatable<TKey>
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElarionIdentityOptions();
        configure?.Invoke(options);

        var identityBuilder = services
            .AddIdentity<TUser, TRole>(identityOptions => configureIdentity?.Invoke(identityOptions))
            .AddEntityFrameworkStores<TDbContext>();
        if (options.AddDefaultTokenProviders) {
            identityBuilder.AddDefaultTokenProviders();
        }

        // Identity issues the standard ClaimTypes; map the current-user snapshot to them (overridable).
        services.AddElarionCurrentUser(currentUser => {
            currentUser.UserIdClaimType = ClaimTypes.NameIdentifier;
            currentUser.EmailClaimType = ClaimTypes.Email;
            currentUser.RoleClaimType = ClaimTypes.Role;
            options.ConfigureCurrentUser?.Invoke(currentUser);
        });

        // The transport-neutral ClaimsAuthorizer enforces [Require*] against ICurrentUser.
        services.AddElarionAuthorization(authorization => {
            authorization.PermissionClaimType = options.Authorization.PermissionClaimType;
            authorization.ForbiddenMessageFormat = options.Authorization.ForbiddenMessageFormat;
            authorization.UnauthorizedMessage = options.Authorization.UnauthorizedMessage;
        });

        return identityBuilder;
    }
}
