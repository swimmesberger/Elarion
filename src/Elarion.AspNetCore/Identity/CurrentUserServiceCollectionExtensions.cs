using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Identity;
using Elarion.JsonRpc;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Service registration helpers for ASP.NET-backed current-user access.
/// </summary>
public static class CurrentUserServiceCollectionExtensions {
    /// <summary>
    /// Registers the default claims-backed <see cref="ICurrentUser"/> implementation.
    /// </summary>
    public static IServiceCollection AddElarionCurrentUser(
        this IServiceCollection services,
        Action<AspNetCoreCurrentUserOptions>? configure = null) {
        if (configure is not null) {
            services.Configure(configure);
        } else {
            services.AddOptions<AspNetCoreCurrentUserOptions>();
        }

        services.TryAddScoped<CurrentUserSnapshot>();
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserSnapshot>());

        // Seed the snapshot into the per-call child scopes the JSON-RPC / MCP dispatchers create, where the
        // request-scope snapshot from CurrentUserMiddleware is not visible. TryAddEnumerable so multiple
        // IDispatchScopeInitializer instances (e.g. a host's tenant/correlation seeders) compose.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDispatchScopeInitializer, CurrentUserScopeInitializer>());

        return services;
    }
}
