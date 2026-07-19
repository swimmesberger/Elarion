using System.Data.Common;
using Elarion.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Sql;

/// <summary>
/// Registers the EF-free SQL tier's scoped data-access services: the <see cref="ISqlSession"/> a handler injects,
/// and — via <see cref="AddElarionSqlUnitOfWork"/> — the <see cref="IUnitOfWork"/> that makes the framework
/// transaction and idempotency decorators wrap a handler's raw-SQL writes in a real database transaction. The
/// AOT/EF-free counterpart to <c>AddElarionUnitOfWork&lt;TDbContext&gt;()</c>.
/// </summary>
public static class SqlServiceCollectionExtensions {
    /// <summary>
    /// Registers the scoped <see cref="ISqlSession"/> over the container's <see cref="DbDataSource"/> (for example
    /// via Npgsql's <c>AddNpgsqlDataSource</c>). This is the handler-facing data-access service; with no unit of
    /// work registered every call runs autonomously (per-call auto-commit), the same semantics a pooled
    /// connection gives. Call <see cref="AddElarionSqlUnitOfWork"/> instead to also get transactional handlers.
    /// </summary>
    public static IServiceCollection AddElarionSqlSession(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // The session is the shared state (connection + current transaction); ISqlSession and (when registered)
        // the unit of work resolve the SAME scoped instance so a handler's reads/writes and the transaction act
        // on one connection.
        services.TryAddScoped(sp => new SqlSession(sp.GetRequiredService<DbDataSource>()));
        services.TryAddScoped<ISqlSession>(sp => sp.GetRequiredService<SqlSession>());

        return services;
    }

    /// <summary>
    /// Registers the scoped session (<see cref="AddElarionSqlSession"/>) and a <see cref="SqlUnitOfWork"/> as the
    /// scoped <see cref="IUnitOfWork"/>, replacing any default (in-memory no-op) unit of work so a handler's
    /// raw-SQL writes commit atomically. Requires a <see cref="DbDataSource"/> in the container.
    /// </summary>
    public static IServiceCollection AddElarionSqlUnitOfWork(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElarionSqlSession();
        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => new SqlUnitOfWork(sp.GetRequiredService<SqlSession>()));

        return services;
    }
}
