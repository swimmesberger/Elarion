using Elarion.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>Registers the PostgreSQL migration runner (ADR-0057).</summary>
public static class MigrationsServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IMigrationRunner"/> plus — unless
    /// <see cref="MigrationOptions.ApplyOnStartup"/> is disabled — a hosted service that applies pending
    /// migrations before the host reports ready and fails startup on error. Register it before other
    /// hosted services that expect the schema (hosted services start in registration order).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The connection string of the database to migrate; the runner opens one dedicated connection per run.</param>
    /// <param name="configure">Configures the options; must add at least one script source via <see cref="MigrationOptions.AddScripts"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSqlMigrations(
    ///     builder.Configuration.GetConnectionString("Default")!,
    ///     o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionPostgreSqlMigrations(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlMigrationOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return AddCore(services, configure, (options, provider) => new PostgreSqlMigrationRunner(
            connectionString, options, provider.GetService<ILogger<PostgreSqlMigrationRunner>>()));
    }

    /// <summary>
    /// The <see cref="NpgsqlDataSource"/> overload of
    /// <see cref="AddElarionPostgreSqlMigrations(IServiceCollection, string, Action{PostgreSqlMigrationOptions})"/>
    /// for hosts that already manage a data source; the runner borrows connections and never disposes it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The data source of the database to migrate.</param>
    /// <param name="configure">Configures the options; must add at least one script source via <see cref="MigrationOptions.AddScripts"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlMigrations(
        this IServiceCollection services,
        NpgsqlDataSource dataSource,
        Action<PostgreSqlMigrationOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        return AddCore(services, configure, (options, provider) => new PostgreSqlMigrationRunner(
            dataSource, options, provider.GetService<ILogger<PostgreSqlMigrationRunner>>()));
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        Action<PostgreSqlMigrationOptions> configure,
        Func<PostgreSqlMigrationOptions, IServiceProvider, IMigrationRunner> runnerFactory) {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PostgreSqlMigrationOptions();
        configure(options);

        return services.AddElarionMigrationRunner(options, provider => runnerFactory(options, provider));
    }
}
