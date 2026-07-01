using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Registers the durable PostgreSQL tus staging store.
/// </summary>
public static class PostgreSqlTusServiceCollectionExtensions {
    /// <summary>
    /// Replaces the in-memory tus store with the durable PostgreSQL staging store backed by
    /// <typeparamref name="TDbContext"/>, and registers the background collector for expired sessions.
    /// </summary>
    /// <typeparam name="TDbContext">The context whose model includes <see cref="TusUploadRow"/> via <c>UseElarionTusStorage</c>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="TusGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Call after <c>AddElarionTus</c> (which registers <c>TusOptions</c> and the in-memory default this
    /// replaces) and after registering an <see cref="IBlobStore"/>.
    /// </remarks>
    public static IServiceCollection AddElarionTusPostgreSql<TDbContext>(
        this IServiceCollection services,
        Action<TusGcOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        // Replace the in-memory default registered by AddElarionTus.
        services.RemoveAll<ITusUploadStore>();
        services.AddScoped<ITusUploadStore, PostgreSqlTusUploadStore<TDbContext>>();

        var options = new TusGcOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddHostedService<TusUploadGarbageCollector>();

        return services;
    }
}
