using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Registers the PostgreSQL-backed blob storage implementation.
/// </summary>
/// <remarks>
/// The store needs an <see cref="NpgsqlDataSource"/> for streaming reads (each read owns a dedicated
/// connection that lives as long as the caller reads — the scoped <c>DbContext</c> connection cannot host
/// that). The parameterless overloads resolve the data source from DI (register one with
/// <c>AddNpgsqlDataSource</c> or by hand); the <c>connectionString</c> overloads register a shared
/// <see cref="NpgsqlDataSource"/> singleton via <c>TryAdd</c>, so a data source the host already registered
/// wins. Either way, it must target the same database as the blob <c>DbContext</c>.
/// </remarks>
public static class PostgreSqlBlobStoreServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IBlobStore"/> using <see cref="PostgreSqlBlobStore{TDbContext}"/>. Requires an
    /// <see cref="NpgsqlDataSource"/> in the container (see the class remarks); use the
    /// <c>connectionString</c> overload to register one in the same call.
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
    /// Registers <see cref="IBlobStore"/> using <see cref="PostgreSqlBlobStore{TDbContext}"/>, plus a shared
    /// <see cref="NpgsqlDataSource"/> for <paramref name="connectionString"/> (via <c>TryAdd</c>, so a
    /// host-registered data source wins) that the store's streaming reads draw dedicated connections from.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add blob storage to.</param>
    /// <param name="connectionString">The connection string of the database the blob tables live in.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlBlobStore<TDbContext>(
        this IServiceCollection services,
        string connectionString)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services.AddElarionPostgreSqlBlobStore<TDbContext>();
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

    /// <summary>
    /// The <c>connectionString</c> overload of
    /// <see cref="AddElarionPostgreSqlBlobLifecycle{TDbContext}(IServiceCollection, Action{BlobGcOptions}?)"/>:
    /// also registers a shared <see cref="NpgsqlDataSource"/> (via <c>TryAdd</c>) for streaming reads.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
    /// <param name="services">The service collection to add the lifecycle to.</param>
    /// <param name="connectionString">The connection string of the database the blob tables live in.</param>
    /// <param name="configure">Optional configuration of <see cref="BlobGcOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlBlobLifecycle<TDbContext>(
        this IServiceCollection services,
        string connectionString,
        Action<BlobGcOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        return services.AddElarionPostgreSqlBlobLifecycle<TDbContext>(configure);
    }
}
