using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Sql.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// Registration tests for the neutral <c>AddElarionMigrations</c> over the PostgreSQL provider chosen by the
/// single <c>AddElarionPostgreSql</c> call — no container needed (building the runner does not open a
/// connection).
/// </summary>
public sealed class MigrationsServiceCollectionExtensionsTests {
    private const string ConnectionString = "Host=localhost;Database=app";

    [Fact]
    public void RegistersRunnerAndStartupHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSql(ConnectionString);

        services.AddElarionMigrations(
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Basic."));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().Should().BeOfType<MigrationRunner>();
        provider.GetServices<IHostedService>().Should().ContainSingle();
    }

    [Fact]
    public void ApplyOnStartupFalse_SkipsTheHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSql(ConnectionString);

        services.AddElarionMigrations(
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
        services.AddElarionPostgreSql(ConnectionString);
        services.AddElarionMigrations(
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Basic."));

        var act = () => services.AddElarionMigrations(
            o => o.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly,
                MigrationScriptDiscoveryTests.ScriptPrefix + "Baseline."));

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void WithoutScriptSources_FailsAtRegistration() {
        var services = new ServiceCollection();

        var act = () => services.AddElarionMigrations(_ => { });

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddScripts*");
    }

    [Fact]
    public void Schema_BecomesTheDataSourceSearchPath_SoQueriesAndMigrationsShareIt() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSql(ConnectionString, schema: "app");

        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).SearchPath.Should().Be("app");
    }

    [Fact]
    public void Schema_DoesNotOverrideAnExplicitSearchPathInTheConnectionString() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSql(ConnectionString + ";Search Path=chosen", schema: "app");

        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).SearchPath.Should().Be("chosen");
    }

    [Fact]
    public void WithoutSchema_LeavesTheConnectionAtTheServerDefault() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSql(ConnectionString);

        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).SearchPath.Should().BeNullOrEmpty();
    }

    [Fact]
    public void InvalidHistoryTableName_FailsAtRunnerConstruction() {
        var options = new MigrationOptions { HistoryTableName = "bad name; drop" };
        options.AddScripts(typeof(MigrationsServiceCollectionExtensionsTests).Assembly);

        var act = () => new PostgreSqlMigrationRunner(ConnectionString, options);

        act.Should().Throw<MigrationException>().WithMessage("*bad name; drop*");
    }
}
