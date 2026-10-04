using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Migrations.EntityFrameworkCore;
using Elarion.Sql.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// ADR-0081 on the EF tier: EF Core migrations are plan steps, so an EF expand migration, a C# backfill and an
/// EF contract migration interleave in one ordered sequence, one history and one lock — and a database EF
/// already migrated is adopted without a baseline.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationPlanEntityFrameworkTests(PostgreSqlMigrationsFixture fixture)
    : IClassFixture<PostgreSqlMigrationsFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_InterleavesEfMigrationsWithACodeBackfill() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        await using var provider = BuildProvider(connectionString);
        var runner = provider.GetRequiredService<IMigrationRunner>();

        var pending = await runner.GetPendingAsync(TestToken);
        pending.Select(p => $"{p.Kind}:{p.Version}").Should().Equal(
            "ef:20260901000100", "code:20260901000200", "ef:20260901000300");
        pending[0].Name.Should().Be("20260901000100_CreateWidgets");
        pending[0].Description.Should().Be("CreateWidgets");

        var applied = await runner.MigrateAsync(TestToken);

        // The contract migration (slug NOT NULL) only succeeds because the backfill ran between the two EF steps.
        applied.Select(a => a.Outcome).Should().OnlyContain(o => o == MigrationOutcome.Applied);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM widgets WHERE slug IN ('alpha', 'beta')"))
            .Should().Be(2L);
        (await ScalarAsync(connectionString, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'widgets' AND column_name = 'slug'"))
            .Should().Be("NO");

        // One Elarion history for all three kinds; EF's own table was kept truthful by the generated script.
        (await StringsAsync(connectionString, "SELECT kind || ':' || step_name FROM elarion_schema_history ORDER BY installed_rank"))
            .Should().Equal(
                "ef:20260901000100_CreateWidgets", "code:WidgetSlugBackfill", "ef:20260901000300_RequireSlug");
        (await StringsAsync(connectionString, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\""))
            .Should().Equal("20260901000100_CreateWidgets", "20260901000300_RequireSlug");

        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        (await runner.ValidateAsync(TestToken)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_AdoptsADatabaseEfAlreadyMigrated_RecordingItsEfStepsAsSatisfied() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        await using var provider = BuildProvider(connectionString);

        // The first EF migration ran the pre-Elarion way, through EF's own migrator.
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WidgetDbContext>();
            await db.GetService<IMigrator>().MigrateAsync("20260901000100_CreateWidgets", TestToken);
        }

        var runner = provider.GetRequiredService<IMigrationRunner>();
        var applied = await runner.MigrateAsync(TestToken);

        applied.Select(a => $"{a.Step.Version}:{a.Outcome}").Should().Equal(
            "20260901000100:Satisfied", "20260901000200:Applied", "20260901000300:Applied");
    }

    private static ServiceProvider BuildProvider(string connectionString) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WidgetDbContext>(o => o.UseNpgsql(connectionString));
        services.AddElarionPostgreSql(connectionString);
        services.AddCodeMigration<WidgetSlugBackfill>();
        services.AddElarionMigrations(o => {
            o.ApplyOnStartup = false;
            o.AddEntityFrameworkMigrations<WidgetDbContext>();
        });
        return services.BuildServiceProvider();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestToken);
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestToken)) values.Add(reader.GetString(0));

        return values;
    }

    public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options);

    /// <summary>Expand: adds the table and a nullable column the backfill will fill.</summary>
    [DbContext(typeof(WidgetDbContext))]
    [Migration("20260901000100_CreateWidgets")]
    public sealed class CreateWidgets : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                "widgets",
                table => new {
                    id = table.Column<int>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    slug = table.Column<string>(nullable: true)
                },
                constraints: table => table.PrimaryKey("pk_widgets", x => x.id));
            migrationBuilder.InsertData("widgets", ["id", "name"], ["integer", "text"],
                new object[,] { { 1, "Alpha" }, { 2, "Beta" } });
        }
    }

    /// <summary>Contract: tightens the column the backfill filled.</summary>
    [DbContext(typeof(WidgetDbContext))]
    [Migration("20260901000300_RequireSlug")]
    public sealed class RequireSlug : Migration {
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AlterColumn<string>("slug", "widgets", nullable: false, oldClrType: typeof(string),
                oldNullable: true);
        }
    }

    /// <summary>The C# step between the two EF migrations.</summary>
    public sealed class WidgetSlugBackfill : ICodeMigration {
        public string Version => "20260901000200";

        public string Description => "backfill widget slugs";

        public Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
            return context.ExecuteSqlAsync("UPDATE widgets SET slug = lower(name)", cancellationToken);
        }
    }
}
