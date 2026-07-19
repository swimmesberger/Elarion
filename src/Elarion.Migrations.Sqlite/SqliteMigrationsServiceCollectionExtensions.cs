using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Migrations.Sqlite;

/// <summary>
/// The single SQLite provider registration for the EF-free tier's migrations (ADR-0060):
/// <see cref="AddElarionSqlite"/> picks SQLite by registering the <see cref="IMigrationDatabaseFactory"/> the
/// neutral <c>AddElarionMigrations</c> resolves — the same two-step shape as PostgreSQL's
/// <c>AddElarionPostgreSql</c>. (SQLite is migration-only: there is no <see cref="System.Data.Common.DbDataSource"/>
/// for it, so the SQL access tier does not apply — a SQLite host registers migrations, not
/// <c>AddElarionSqlUnitOfWork</c>.)
/// </summary>
public static class SqliteMigrationsServiceCollectionExtensions {
    /// <summary>
    /// Registers SQLite as the migration provider: the <see cref="IMigrationDatabaseFactory"/> over
    /// <paramref name="connectionString"/> (e.g. <c>Data Source=app.db</c>; the runner opens one dedicated
    /// connection per run). Pair with the neutral <c>AddElarionMigrations(configure)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionSqlite(builder.Configuration.GetConnectionString("Default")!);
    /// builder.Services.AddElarionMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionSqlite(this IServiceCollection services, string connectionString) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IMigrationDatabaseFactory>(new SqliteMigrationDatabaseFactory(connectionString));
        return services;
    }
}
