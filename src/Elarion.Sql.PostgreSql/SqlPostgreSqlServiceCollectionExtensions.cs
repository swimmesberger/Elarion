using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Sql.PostgreSql;

/// <summary>
/// Registers a central PostgreSQL data source for the EF-free SQL tier — the shared core, analogous to how a
/// <c>DbContext</c> is central in EF Core. One <see cref="NpgsqlDataSource"/> backs both the
/// <see cref="ISqlSession"/> access tier (through the <see cref="IElarionSqlDataSourceProvider"/> it registers)
/// and, via <c>Elarion.Migrations.PostgreSql</c>'s data-source-from-DI overload, the migration runner — so a host
/// configures the database once. Keeps <c>Elarion.Sql</c> itself free of any Npgsql reference (ADR-0058); the
/// Npgsql binding lives here, mirroring <c>Elarion.Migrations.PostgreSql</c>.
/// </summary>
public static class SqlPostgreSqlServiceCollectionExtensions {
    /// <summary>
    /// Registers a container-owned <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/>
    /// (with command logging wired from the container's logger factory) and the default
    /// <see cref="IElarionSqlDataSourceProvider"/> over it. Pair with <c>AddElarionSqlUnitOfWork()</c> for the
    /// access tier and <c>AddElarionPostgreSqlMigrations(configure)</c> to share the same source with migrations.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">The connection string of the database.</param>
    /// <param name="configure">
    /// Optional extra configuration of the <see cref="NpgsqlSlimDataSourceBuilder"/> (the NativeAOT-friendly
    /// builder) — for example <c>db =&gt; db.EnableParameterLogging()</c> in development.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionPostgreSqlDataSource(connectionString,
    ///     db => { if (builder.Environment.IsDevelopment()) db.EnableParameterLogging(); });
    /// builder.Services.AddElarionSqlUnitOfWork();
    /// builder.Services.AddElarionPostgreSqlMigrations(o => o.AddScripts(typeof(Program).Assembly, "MyApp.Migrations."));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionPostgreSqlDataSource(
        this IServiceCollection services,
        string connectionString,
        Action<NpgsqlSlimDataSourceBuilder>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<NpgsqlDataSource>(sp => {
            var builder = new NpgsqlSlimDataSourceBuilder(connectionString);
            builder.UseLoggerFactory(sp.GetService<ILoggerFactory>());
            configure?.Invoke(builder);
            return builder.Build();
        });

        // The SQL tier's provider wraps the same NpgsqlDataSource (an upcast to DbDataSource); migrations resolve
        // NpgsqlDataSource directly, so both draw from the one core source.
        return services.AddElarionSqlDataSource(sp => sp.GetRequiredService<NpgsqlDataSource>());
    }

    /// <summary>
    /// The <see cref="NpgsqlDataSource"/> overload of
    /// <see cref="AddElarionPostgreSqlDataSource(IServiceCollection, string, Action{NpgsqlSlimDataSourceBuilder})"/>
    /// for a host that already built its data source; it is registered as the shared core (the container does not
    /// dispose an externally-created instance) and wrapped in the provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">The data source to use as the shared core.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddElarionPostgreSqlDataSource(
        this IServiceCollection services, NpgsqlDataSource dataSource) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton(dataSource);
        return services.AddElarionSqlDataSource(_ => dataSource);
    }
}
