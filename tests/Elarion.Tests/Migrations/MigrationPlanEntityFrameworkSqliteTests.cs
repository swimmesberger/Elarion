using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Migrations.EntityFrameworkCore;
using Elarion.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// EF migrations as plan steps on SQLite (no Docker): raw <c>migrationBuilder.Sql</c> operations without a
/// trailing semicolon apply through the plan exactly as they do through <c>Database.MigrateAsync()</c>.
/// </summary>
public sealed class MigrationPlanEntityFrameworkSqliteTests(SqliteMigrationsFixture fixture)
    : IClassFixture<SqliteMigrationsFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_ExecutesUnterminatedRawSqlOperationsExactlyAsMigrateAsyncDoes() {
        var planned = fixture.CreateConnectionString();
        var viaEf = fixture.CreateConnectionString();

        await using (var provider = BuildProvider(planned)) {
            var applied = await provider.GetRequiredService<IMigrationRunner>().MigrateAsync(TestToken);
            applied.Should().ContainSingle().Which.Outcome.Should().Be(MigrationOutcome.Applied);
        }

        await using (var provider = BuildProvider(viaEf)) {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RawDbContext>().Database.MigrateAsync(TestToken);
        }

        foreach (var connectionString in new[] { planned, viaEf }) {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM raw_items ORDER BY id)";
            (await command.ExecuteScalarAsync(TestToken)).Should().Be("one,two");
        }
    }

    private static ServiceProvider BuildProvider(string connectionString) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RawDbContext>(o => o.UseSqlite(connectionString));
        services.AddElarionSqlite(connectionString);
        services.AddElarionMigrations(o => {
            o.ApplyOnStartup = false;
            o.AddEntityFrameworkMigrations<RawDbContext>();
        });
        return services.BuildServiceProvider();
    }

    public sealed class RawDbContext(DbContextOptions<RawDbContext> options) : DbContext(options);

    [DbContext(typeof(RawDbContext))]
    [Migration("20260903000100_RawStatements")]
    public sealed class RawStatements : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.Sql("CREATE TABLE raw_items (id integer PRIMARY KEY, name text NOT NULL)");
            migrationBuilder.Sql("INSERT INTO raw_items (id, name) VALUES (1, 'one'), (2, 'two')");
        }
    }
}
