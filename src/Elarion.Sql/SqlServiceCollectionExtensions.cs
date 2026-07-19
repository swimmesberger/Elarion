using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Sql;

/// <summary>
/// Registers the EF-free SQL tier's scoped data-access services. Wiring is two steps, mirroring the EF tier
/// (<c>AddDbContext</c> then <c>AddElarionUnitOfWork&lt;TDbContext&gt;</c>):
/// <list type="number">
/// <item>register the <see cref="ISqlDatabase"/> — the application's database handle and the single source of
/// truth for which data source the tier uses — with an <c>AddElarionSqlDatabase</c> overload (a provider
/// package's <c>AddElarionPostgreSql</c>/<c>AddElarionSqlite</c> does it for you); and</item>
/// <item>register the tier with <see cref="AddElarionSqlSession"/> (session only, per-call auto-commit) or
/// <see cref="AddElarionSqlUnitOfWork"/> (transactional, so the framework decorators commit a handler's writes
/// atomically).</item>
/// </list>
/// The session depends on the database handle and nothing else — the tier never resolves a
/// <see cref="DbDataSource"/> itself, so there is no hidden second registration to get wrong.
/// </summary>
public static class SqlServiceCollectionExtensions {
    /// <summary>
    /// Registers the default <see cref="ISqlDatabase"/> over a <see cref="DbDataSource"/> already in the
    /// container — for a host that registered one idiomatically (Npgsql's <c>AddNpgsqlDataSource</c>). Use a
    /// <see cref="AddElarionSqlDatabase(IServiceCollection, Func{IServiceProvider, DbDataSource})">factory</see>
    /// overload instead to build and own the source here.
    /// </summary>
    public static IServiceCollection AddElarionSqlDatabase(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISqlDatabase>(
            sp => new DataSourceSqlDatabase(sp.GetRequiredService<DbDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers the default <see cref="ISqlDatabase"/> over a <see cref="DbDataSource"/> built by
    /// <paramref name="dataSourceFactory"/> — the source is created and owned by the container (disposed on
    /// shutdown), so the host needs no separate <see cref="DbDataSource"/> registration. The common shape when
    /// the source needs services to configure it (a logger factory).
    /// </summary>
    public static IServiceCollection AddElarionSqlDatabase(
        this IServiceCollection services, Func<IServiceProvider, DbDataSource> dataSourceFactory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);

        // Register the source as a container-created singleton so the container disposes it, then wrap it.
        services.TryAddSingleton<DbDataSource>(dataSourceFactory);
        return services.AddElarionSqlDatabase();
    }

    /// <summary>
    /// Registers a custom scoped <see cref="ISqlDatabase"/> — for per-request routing, where
    /// <see cref="ISqlDatabase.GetDataSource"/> reads the current tenant from <c>ICurrentUser</c>
    /// (or similar) and returns that tenant's or replica's pooled data source.
    /// </summary>
    public static IServiceCollection AddElarionSqlDatabase<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDatabase>(
        this IServiceCollection services)
        where TDatabase : class, ISqlDatabase {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ISqlDatabase, TDatabase>();
        return services;
    }

    /// <summary>
    /// Registers the scoped <see cref="ISqlSession"/> a handler injects. Requires an
    /// <see cref="ISqlDatabase"/> (register one with an <c>AddElarionSqlDatabase*</c> method).
    /// With no unit of work registered every call runs autonomously (per-call auto-commit), the same semantics a
    /// pooled connection gives; call <see cref="AddElarionSqlUnitOfWork"/> for transactional handlers.
    /// </summary>
    public static IServiceCollection AddElarionSqlSession(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // The session is the shared state (connection + current transaction); ISqlSession and (when registered)
        // the unit of work resolve the SAME scoped instance so a handler's reads/writes and the transaction act
        // on one connection.
        services.TryAddScoped(sp => new SqlSession(sp.GetRequiredService<ISqlDatabase>()));
        services.TryAddScoped<ISqlSession>(sp => sp.GetRequiredService<SqlSession>());

        return services;
    }

    /// <summary>
    /// Registers the scoped session (<see cref="AddElarionSqlSession"/>) and a <see cref="SqlUnitOfWork"/> as the
    /// scoped <see cref="IUnitOfWork"/>, replacing any default (in-memory no-op) unit of work so a handler's
    /// raw-SQL writes commit atomically. Requires an <see cref="ISqlDatabase"/>.
    /// </summary>
    public static IServiceCollection AddElarionSqlUnitOfWork(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSqlSession();
        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => new SqlUnitOfWork(sp.GetRequiredService<SqlSession>()));

        return services;
    }
}
