using Elarion.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// The single PostgreSQL provider registration for the EF-free tier: <see cref="AddElarionPostgreSql"/> picks
/// PostgreSQL for <b>every</b> subsystem at once. It registers one central <see cref="NpgsqlDataSource"/> — the
/// shared core, analogous to how a <c>DbContext</c> is central in EF Core — plus the
/// <see cref="ISqlDatabase"/> the <see cref="ISqlSession"/> access tier opens from and the
/// <see cref="IMigrationDatabaseFactory"/> the neutral <c>AddElarionMigrations</c> resolves. The neutral
/// <c>AddElarionSqlUnitOfWork</c> and <c>AddElarionMigrations</c> then wire the subsystems without naming a
/// provider, so swapping databases is a one-line change here. Keeps <c>Elarion.Sql</c> and
/// <c>Elarion.Migrations</c> free of any Npgsql reference (ADR-0058).
/// </summary>
public static class SqlPostgreSqlServiceCollectionExtensions {
    /// <summary>
    /// Registers a container-owned <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/>
    /// (command logging wired from the container's logger factory) and, over it, the
    /// <see cref="ISqlDatabase"/> (for the access tier) and the
    /// <see cref="IMigrationDatabaseFactory"/> (for migrations). Pair with the neutral
    /// <c>AddElarionSqlUnitOfWork()</c> and <c>AddElarionMigrations(configure)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The connection string of the database.</param>
    /// <param name="configure">
    /// Optional extra configuration of the <see cref="NpgsqlSlimDataSourceBuilder"/> (the NativeAOT-friendly
    /// builder) — for example <c>db =&gt; db.EnableParameterLogging()</c> in development.
    /// </param>
    /// <param name="advisoryLockKey">
    /// The session-level <c>pg_advisory_lock</c> key serializing concurrent migration runners. Defaults to
    /// <see cref="PostgreSqlMigrationRunner.DefaultAdvisoryLockKey"/>; independent schemas in one database can
    /// pick distinct keys to migrate concurrently.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSql(connectionString,
    ///     db => { if (builder.Environment.IsDevelopment()) db.EnableParameterLogging(); });
    /// builder.Services.AddElarionSqlUnitOfWork();
    /// builder.Services.AddElarionMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionPostgreSql(
        this IServiceCollection services,
        string connectionString,
        Action<NpgsqlSlimDataSourceBuilder>? configure = null,
        long advisoryLockKey = PostgreSqlMigrationRunner.DefaultAdvisoryLockKey) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<NpgsqlDataSource>(sp => {
            var builder = new NpgsqlSlimDataSourceBuilder(connectionString);
            builder.UseLoggerFactory(sp.GetService<ILoggerFactory>());
            configure?.Invoke(builder);
            return builder.Build();
        });

        return services.AddProviderOverDataSource(advisoryLockKey);
    }

    /// <summary>
    /// The <see cref="NpgsqlDataSource"/> overload of
    /// <see cref="AddElarionPostgreSql(IServiceCollection, string, Action{NpgsqlSlimDataSourceBuilder}, long)"/>
    /// for a host that already built its data source; it is registered as the shared core (the container does not
    /// dispose an externally-created instance).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The data source to use as the shared core.</param>
    /// <param name="advisoryLockKey">The migration advisory-lock key (see the connection-string overload).</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSql(
        this IServiceCollection services, NpgsqlDataSource dataSource,
        long advisoryLockKey = PostgreSqlMigrationRunner.DefaultAdvisoryLockKey) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton(dataSource);
        return services.AddProviderOverDataSource(advisoryLockKey);
    }

    private static IServiceCollection AddProviderOverDataSource(this IServiceCollection services, long advisoryLockKey) {
        // The access tier's provider wraps the same NpgsqlDataSource (an upcast to DbDataSource); the migration
        // factory captures it and the advisory-lock key. Both draw from the one core source.
        services.AddElarionSqlDatabase(sp => sp.GetRequiredService<NpgsqlDataSource>());
        services.TryAddSingleton<IMigrationDatabaseFactory>(
            sp => new PostgreSqlMigrationDatabaseFactory(sp.GetRequiredService<NpgsqlDataSource>(), advisoryLockKey));
        return services;
    }
}
