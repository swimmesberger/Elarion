using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.Tus;

/// <summary>
/// Registers the services backing the tus resumable-upload endpoints.
/// </summary>
public static class TusServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="TusOptions"/> and the default in-memory <see cref="ITusUploadStore"/> for the
    /// endpoints mapped by <c>MapElarionTus</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="TusOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The endpoints additionally require an <see cref="IBlobStore"/> and an <c>ICurrentUser</c> from the
    /// host. Replace the in-memory store with a durable one (for example PostgreSQL staging via
    /// <c>AddElarionTusPostgreSql</c>) for resumability across restarts and instances by registering an
    /// <see cref="ITusUploadStore"/> before calling this (it uses <c>TryAdd</c>).
    /// </remarks>
    public static IServiceCollection AddElarionTus(
        this IServiceCollection services,
        Action<TusOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TusOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        // Singleton: the in-memory store retains staging state across the requests of one upload.
        services.TryAddSingleton<ITusUploadStore, InMemoryTusUploadStore>();

        return services;
    }
}
