using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Migrations.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Migrations;

public sealed class MigrationsServiceCollectionExtensionsTests {
    [Fact]
    public void RegistersRunnerAndStartupHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElarionPostgreSqlMigrations(
            "Host=localhost;Database=app",
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Basic."));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Should().BeOfType<PostgreSqlMigrationRunner>();
        provider.GetServices<IHostedService>().Should().ContainSingle();
    }

    [Fact]
    public void ApplyOnStartupFalse_SkipsTheHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElarionPostgreSqlMigrations(
            "Host=localhost;Database=app",
            o => {
                o.ApplyOnStartup = false;
                o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                    MigrationScriptDiscoveryTests.ScriptPrefix + "Basic.");
            });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Should().NotBeNull();
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    [Fact]
    public void SecondRegistration_FailsLoud() {
        var services = new ServiceCollection();
        services.AddElarionPostgreSqlMigrations(
            "Host=localhost;Database=app",
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Basic."));

        var act = () => services.AddElarionPostgreSqlMigrations(
            "Host=localhost;Database=other",
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Baseline."));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void WithoutScriptSources_FailsAtRegistration() {
        var services = new ServiceCollection();

        var act = () => services.AddElarionPostgreSqlMigrations("Host=localhost;Database=app", _ => { });

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddScripts*");
    }

    [Fact]
    public void InvalidHistoryTableName_FailsAtRunnerConstruction() {
        var options = new PostgreSqlMigrationOptions { HistoryTableName = "bad name; drop" };
        options.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly);

        var act = () => new PostgreSqlMigrationRunner("Host=localhost;Database=app", options);

        act.Should().Throw<MigrationException>().WithMessage("*bad name; drop*");
    }
}
