using System.Data.Common;
using Elarion.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Sql;

/// <summary>
/// Registers the EF-free SQL tier's scoped data-access services: the <see cref="ISqlSession"/> a handler injects,
/// and — via <see cref="AddElarionSqlUnitOfWork(IServiceCollection)"/> — the <see cref="IUnitOfWork"/> that makes
/// the framework transaction and idempotency decorators wrap a handler's raw-SQL writes in a real database
/// transaction. The AOT/EF-free counterpart to <c>AddElarionUnitOfWork&lt;TDbContext&gt;()</c>.
/// </summary>
/// <remarks>
/// Which data source the session opens from is the <see cref="IElarionSqlDataSourceProvider"/> seam. The no-source
/// overloads register a default provider over a container-registered <see cref="DbDataSource"/>; the factory
/// overloads name the source explicitly (no need to register it as <see cref="DbDataSource"/> separately); and a
/// host that registers its own scoped <see cref="IElarionSqlDataSourceProvider"/> — for tenant or replica routing
/// — wins over the default, because the registration is <c>TryAdd</c>.
/// </remarks>
public static class SqlServiceCollectionExtensions {
    /// <summary>
    /// Registers the scoped <see cref="ISqlSession"/>, defaulting the <see cref="IElarionSqlDataSourceProvider"/>
    /// to one over the container's <see cref="DbDataSource"/> (for example via Npgsql's <c>AddNpgsqlDataSource</c>).
    /// This is the handler-facing data-access service; with no unit of work registered every call runs
    /// autonomously (per-call auto-commit), the same semantics a pooled connection gives. Call
    /// <see cref="AddElarionSqlUnitOfWork(IServiceCollection)"/> instead to also get transactional handlers.
    /// </summary>
    public static IServiceCollection AddElarionSqlSession(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // The session is the shared state (connection + current transaction); ISqlSession and (when registered)
        // the unit of work resolve the SAME scoped instance so a handler's reads/writes and the transaction act
        // on one connection.
        services.TryAddScoped(sp => new SqlSession(sp.GetRequiredService<IElarionSqlDataSourceProvider>()));
        services.TryAddScoped<ISqlSession>(sp => sp.GetRequiredService<SqlSession>());

        // Default provider over a container-registered DbDataSource. TryAdd so a host-supplied provider (a
        // tenant/replica router, or one named through the factory overload) wins.
        services.TryAddSingleton<IElarionSqlDataSourceProvider>(
            sp => new SingletonSqlDataSourceProvider(sp.GetRequiredService<DbDataSource>()));

        return services;
    }

    /// <summary>
    /// Registers the scoped <see cref="ISqlSession"/> over the data source <paramref name="dataSourceFactory"/>
    /// resolves — so a host can point at a specific <see cref="DbDataSource"/> (for example
    /// <c>sp =&gt; sp.GetRequiredService&lt;NpgsqlDataSource&gt;()</c>) without also registering it as
    /// <see cref="DbDataSource"/>.
    /// </summary>
    public static IServiceCollection AddElarionSqlSession(
        this IServiceCollection services, Func<IServiceProvider, DbDataSource> dataSourceFactory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);

        services.TryAddSingleton<IElarionSqlDataSourceProvider>(
            sp => new SingletonSqlDataSourceProvider(dataSourceFactory(sp)));

        return services.AddElarionSqlSession();
    }

    /// <summary>
    /// Registers the scoped session (<see cref="AddElarionSqlSession(IServiceCollection)"/>) and a
    /// <see cref="SqlUnitOfWork"/> as the scoped <see cref="IUnitOfWork"/>, replacing any default (in-memory
    /// no-op) unit of work so a handler's raw-SQL writes commit atomically. Requires a <see cref="DbDataSource"/>
    /// in the container, or an <see cref="IElarionSqlDataSourceProvider"/> registered separately.
    /// </summary>
    public static IServiceCollection AddElarionSqlUnitOfWork(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSqlSession();
        return services.AddSqlUnitOfWorkCore();
    }

    /// <summary>
    /// Registers the scoped session over the data source <paramref name="dataSourceFactory"/> resolves and the
    /// transactional unit of work — the factory-overload counterpart to
    /// <see cref="AddElarionSqlUnitOfWork(IServiceCollection)"/>.
    /// </summary>
    public static IServiceCollection AddElarionSqlUnitOfWork(
        this IServiceCollection services, Func<IServiceProvider, DbDataSource> dataSourceFactory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);

        services.AddElarionSqlSession(dataSourceFactory);
        return services.AddSqlUnitOfWorkCore();
    }

    private static IServiceCollection AddSqlUnitOfWorkCore(this IServiceCollection services) {
        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => new SqlUnitOfWork(sp.GetRequiredService<SqlSession>()));
        return services;
    }
}
