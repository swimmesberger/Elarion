using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-backed blob storage implementation.
/// </summary>
/// <remarks>
/// The store needs no connection configuration of its own: streaming reads open a dedicated
/// connection cloned from the blob <c>DbContext</c>'s connection (same pool, same type mapping, same
/// auth callbacks, same database by construction — ADR-0041), so registering the context is the only
/// wiring.
/// </remarks>
public static class PostgreSqlBlobStoreServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="PostgreSqlBlobStore{TDbContext}"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlBlobStore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IBlobStore, PostgreSqlBlobStore<TDbContext>>();
        return services;
    }

    /// <summary>
    /// Registers the blob lifecycle (<see cref="IBlobLifecycle"/>) plus the background garbage collector
    /// that reclaims expired pending blobs, on top of
    /// <see cref="AddElarionPostgreSqlBlobStore{TDbContext}(IServiceCollection)"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add the lifecycle to.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <see cref="IBlobLifecycle"/> is the same scoped <see cref="PostgreSqlBlobStore{TDbContext}"/> as
    /// <see cref="IBlobStore"/>, so it shares the caller's <c>DbContext</c> and a commit participates in
    /// the caller's transaction. Use this instead of
    /// <see cref="AddElarionPostgreSqlBlobStore{TDbContext}(IServiceCollection)"/>
    /// when an upload transport produces pending blobs that must be committed or reclaimed.
    /// </remarks>
    public static IServiceCollection AddElarionPostgreSqlBlobLifecycle<TDbContext>(
        this IServiceCollection services,
        Action<BlobGcOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionPostgreSqlBlobStore<TDbContext>();

        services.TryAddScoped<IBlobLifecycle, PostgreSqlBlobStore<TDbContext>>();
        // Idempotent, so repeated wiring (for example via AddElarionPostgreSqlStagedUploads) never
        // registers a second collector.
        services.AddElarionBlobGarbageCollection(configure);

        return services;
    }
}
