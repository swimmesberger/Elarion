using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Elarion.Abstractions.Caching;
using Elarion.Abstractions.Serialization;

namespace Elarion.Caching;

/// <summary>
/// Service registration helpers for generated handler caching.
/// </summary>
public static class HandlerCacheServiceCollectionExtensions {
    /// <summary>
    /// Adds the default HybridCache-backed handler cache implementation.
    /// </summary>
    public static IServiceCollection AddElarionHandlerCaching(this IServiceCollection services) {
        services.AddElarionJson();
        services.AddHybridCache();
        services.TryAddScoped<IHandlerCache, HybridHandlerCache>();

        return services;
    }
}
