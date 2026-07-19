using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Migrations.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// Registration tests for the neutral <c>AddElarionMigrations</c> over the SQLite provider chosen by
/// <c>AddElarionSqlite</c> — the same two-step shape as PostgreSQL.
/// </summary>
public sealed class SqliteMigrationsServiceCollectionExtensionsTests {
    private const string ConnectionString = "Data Source=app.db";

    [Fact]
    public void RegistersRunnerAndStartupHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionSqlite(ConnectionString);

        services.AddElarionMigrations(
            o => o.AddScripts(typeof(SqliteMigrationRunnerIntegrationTests).Assembly,
                SqliteMigrationRunnerIntegrationTests.ScriptPrefix + "Basic."));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Should().BeOfType<MigrationRunner>();
        provider.GetServices<IHostedService>().Should().ContainSingle();
    }

    [Fact]
    public void ApplyOnStartupFalse_SkipsTheHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionSqlite(ConnectionString);

        services.AddElarionMigrations(
            o => {
                o.ApplyOnStartup = false;
                o.AddScripts(typeof(SqliteMigrationRunnerIntegrationTests).Assembly,
                    SqliteMigrationRunnerIntegrationTests.ScriptPrefix + "Basic.");
            });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Should().NotBeNull();
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    [Fact]
    public void WithoutScriptSources_FailsAtRegistration() {
        var services = new ServiceCollection();

        var act = () => services.AddElarionMigrations(_ => { });

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddScripts*");
    }
}
