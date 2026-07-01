using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-backed blob storage implementation.
/// </summary>
public static class PostgreSqlBlobStoreServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="PostgreSqlBlobStore{TDbContext}"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPostgreSqlBlobStore<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IBlobStore, PostgreSqlBlobStore<TDbContext>>();
        return services;
    }

    /// <summary>
    /// Registers the blob lifecycle (<see cref="IBlobLifecycle"/>) plus the background garbage collector
    /// that reclaims expired pending blobs, on top of <see cref="AddPostgreSqlBlobStore{TDbContext}"/>.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add the lifecycle to.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <see cref="IBlobLifecycle"/> is the same scoped <see cref="PostgreSqlBlobStore{TDbContext}"/> as
    /// <see cref="IBlobStore"/>, so it shares the caller's <c>DbContext</c> and a commit participates in
    /// the caller's transaction. Use this instead of <see cref="AddPostgreSqlBlobStore{TDbContext}"/>
    /// when an upload transport produces pending blobs that must be committed or reclaimed.
    /// </remarks>
    public static IServiceCollection AddPostgreSqlBlobLifecycle<TDbContext>(
        this IServiceCollection services,
        Action<BlobGcOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPostgreSqlBlobStore<TDbContext>();

        var options = new BlobGcOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddScoped<IBlobLifecycle, PostgreSqlBlobStore<TDbContext>>();
        services.AddHostedService<BlobGarbageCollector>();

        return services;
    }
}
