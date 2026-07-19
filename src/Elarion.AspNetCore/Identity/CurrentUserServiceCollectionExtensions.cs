using Elarion.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Service registration helpers for ASP.NET-backed current-user access.
/// </summary>
public static class CurrentUserServiceCollectionExtensions {
    /// <summary>
    /// Registers the default claims-backed <see cref="Abstractions.Identity.ICurrentUser"/> implementation.
    /// This is the ASP.NET-host-facing alias for the transport-neutral
    /// <see cref="ClaimsCurrentUserServiceCollectionExtensions.AddElarionClaimsCurrentUser"/>; pair it with
    /// <c>UseElarionCurrentUser()</c> so the authenticated request principal is captured into the request scope.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional claim-type mapping configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddElarionCurrentUser(
        this IServiceCollection services,
        Action<ClaimsCurrentUserOptions>? configure = null) {
        return services.AddElarionClaimsCurrentUser(configure);
    }
}
