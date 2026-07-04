using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Blobs;

/// <summary>
/// Registers the provider-neutral pending-blob garbage collection. Provider packages call this from
/// their lifecycle registration; it composes with whatever <see cref="IBlobLifecycle"/> they register.
/// </summary>
public static class BlobLifecycleServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="BlobGcOptions"/> and the <see cref="BlobGarbageCollector"/> hosted service.
    /// Idempotent: the collector is added once, and an explicit <paramref name="configure"/> replaces
    /// previously registered options while <c>null</c> keeps them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionBlobGarbageCollection(
        this IServiceCollection services,
        Action<BlobGcOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null) {
            var options = new BlobGcOptions();
            configure(options);
            services.RemoveAll<BlobGcOptions>();
            services.AddSingleton(options);
        }
        else {
            services.TryAddSingleton(new BlobGcOptions());
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BlobGarbageCollector>());

        return services;
    }
}
