using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Blobs;

/// <summary>
/// Registers the staged (resumable) upload seam with its in-memory default.
/// </summary>
public static class StagedUploadServiceCollectionExtensions {
    /// <summary>
    /// Registers the default in-memory <see cref="IStagedUploadStore"/> (via <c>TryAdd</c>, so a durable
    /// provider store registered earlier or later wins) and the background collector that reclaims
    /// expired sessions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureGc">Optional configuration of <see cref="StagedUploadGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Upload transports (for example tus via <c>AddElarionResumableBlobUploads</c>) call this themselves; a durable
    /// backend replaces the store with <c>AddElarionPostgreSqlStagedUploads</c> or
    /// <c>AddElarionAzureStagedUploads</c>. Completion additionally requires an <see cref="IBlobStore"/>
    /// from the host.
    /// </remarks>
    public static IServiceCollection AddElarionStagedUploads(
        this IServiceCollection services,
        Action<StagedUploadGcOptions>? configureGc = null) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        // Singleton: the in-memory store retains staging state across the requests of one upload.
        services.TryAddSingleton<IStagedUploadStore, InMemoryStagedUploadStore>();
        services.AddElarionStagedUploadGarbageCollection(configureGc);

        return services;
    }

    /// <summary>
    /// Registers <see cref="StagedUploadGcOptions"/> and the <see cref="StagedUploadGarbageCollector"/>
    /// hosted service. Idempotent: the collector is added once, and an explicit
    /// <paramref name="configureGc"/> replaces previously registered options while <c>null</c> keeps them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureGc">Optional configuration of <see cref="StagedUploadGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionStagedUploadGarbageCollection(
        this IServiceCollection services,
        Action<StagedUploadGcOptions>? configureGc = null) {
        ArgumentNullException.ThrowIfNull(services);

        if (configureGc is not null) {
            var options = new StagedUploadGcOptions();
            configureGc(options);
            services.RemoveAll<StagedUploadGcOptions>();
            services.AddSingleton(options);
        }
        else {
            services.TryAddSingleton(new StagedUploadGcOptions());
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StagedUploadGarbageCollector>());

        return services;
    }
}
