using Elarion.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Migrations.Sqlite;

/// <summary>Registers the SQLite migration runner (ADR-0060).</summary>
public static class SqliteMigrationsServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IMigrationRunner"/> plus — unless
    /// <see cref="MigrationOptions.ApplyOnStartup"/> is disabled — a hosted service that applies pending
    /// migrations before the host reports ready and fails startup on error. Register it before other
    /// hosted services that expect the schema (hosted services start in registration order).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The SQLite connection string (e.g. <c>Data Source=app.db</c>); the runner opens one dedicated connection per run.</param>
    /// <param name="configure">Configures the options; must add at least one script source via <see cref="MigrationOptions.AddScripts"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionSqliteMigrations(
    ///     builder.Configuration.GetConnectionString("Default")!,
    ///     o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionSqliteMigrations(
        this IServiceCollection services,
        string connectionString,
        Action<SqliteMigrationOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqliteMigrationOptions();
        configure(options);

        return services.AddElarionMigrationRunner(options, provider => new SqliteMigrationRunner(
            connectionString, options, provider.GetService<ILogger<SqliteMigrationRunner>>()));
    }
}
