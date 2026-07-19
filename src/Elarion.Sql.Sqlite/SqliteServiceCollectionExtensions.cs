using System.Data.Common;
using Elarion.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Sql.Sqlite;

/// <summary>
/// The single SQLite provider registration for the EF-free tier: <see cref="AddElarionSqlite"/> picks SQLite for
/// <b>every</b> subsystem at once, mirroring <c>AddElarionPostgreSql</c>. It registers a
/// <see cref="DbDataSource"/> over <c>Microsoft.Data.Sqlite</c> plus the <see cref="IElarionSqlDataSourceProvider"/>
/// the <see cref="ISqlSession"/> access tier opens from, and the <see cref="IMigrationDatabaseFactory"/> the neutral
/// <c>AddElarionMigrations</c> resolves. The neutral <c>AddElarionSqlUnitOfWork</c> and <c>AddElarionMigrations</c>
/// wire the subsystems without naming a provider.
/// </summary>
public static class SqliteServiceCollectionExtensions {
    /// <summary>
    /// Registers SQLite as the provider for the EF-free tier over <paramref name="connectionString"/> (e.g.
    /// <c>Data Source=app.db</c>): a <see cref="DbDataSource"/> and the <see cref="IElarionSqlDataSourceProvider"/>
    /// for the access tier, and the <see cref="IMigrationDatabaseFactory"/> for migrations. Pair with the neutral
    /// <c>AddElarionSqlUnitOfWork()</c> and <c>AddElarionMigrations(configure)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionSqlite(builder.Configuration.GetConnectionString("Default")!);
    /// builder.Services.AddElarionSqlUnitOfWork();
    /// builder.Services.AddElarionMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionSqlite(this IServiceCollection services, string connectionString) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // The access tier opens pooled connections from this DbDataSource; migrations open their own dedicated
        // pooling-disabled connection (the in-process per-file lock needs it) — both target the same file.
        services.TryAddSingleton<DbDataSource>(_ => new SqliteDataSource(connectionString));
        services.AddElarionSqlDataSource(sp => sp.GetRequiredService<DbDataSource>());
        services.TryAddSingleton<IMigrationDatabaseFactory>(new SqliteMigrationDatabaseFactory(connectionString));

        return services;
    }
}
