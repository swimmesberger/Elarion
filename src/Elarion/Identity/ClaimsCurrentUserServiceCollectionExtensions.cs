using Elarion.Abstractions.Identity;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Identity;

/// <summary>
/// Registers the transport-neutral, claims-based <see cref="ICurrentUser"/> implementation
/// (<see cref="ClaimsPrincipalCurrentUser"/>) and its dispatch-scope initializer. No ASP.NET dependency, so a
/// gRPC, console, or any custom transport gets <c>ICurrentUser</c> + authorization by referencing only
/// <c>Elarion</c>. An HTTP host typically calls <c>AddElarionCurrentUser</c> (which delegates here) plus
/// <c>UseElarionCurrentUser</c>.
/// </summary>
public static class ClaimsCurrentUserServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="ClaimsPrincipalCurrentUser"/> as the scoped <see cref="ICurrentUser"/> and the
    /// initializer that seeds it per dispatch call from the <c>ClaimsPrincipal</c> captured in the
    /// <see cref="DispatchScopeContext"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional claim-type mapping configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionClaimsCurrentUser(
        this IServiceCollection services,
        Action<ClaimsCurrentUserOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ClaimsCurrentUserOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<ClaimsPrincipalCurrentUser>();
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<ClaimsPrincipalCurrentUser>());

        // Seed the snapshot into the per-call dispatch scopes (and, on an HTTP host, the request scope via the
        // middleware) from the captured principal. TryAddEnumerable so a host's own initializers compose.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDispatchScopeInitializer, CurrentUserScopeInitializer>());

        return services;
    }
}
