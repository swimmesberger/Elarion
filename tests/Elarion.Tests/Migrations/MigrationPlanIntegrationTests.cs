using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Sql.PostgreSql;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// ADR-0081 against real PostgreSQL: SQL scripts and C# code steps share one version sequence, one history
/// and one lock — interleaved ordering, transactional rollback and retry of a code step, exactly-once under
/// racing runners, satisfied/baseline outcomes, and the in-place upgrade of a script-shaped history table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationPlanIntegrationTests(PostgreSqlMigrationsFixture fixture)
    : IClassFixture<PostgreSqlMigrationsFixture> {
    private const string Backfill = "20260901000200";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_InterleavesSqlCodeAndSqlByVersion_AndRepeatablesRunLast() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var runner = CreateRunner(connectionString, "Interleaved.", BackfillStep());

        var pending = await runner.GetPendingAsync(TestToken);
        pending.Select(p => p.Kind).Should().Equal("sql", "code", "sql", "sql");

        var applied = await runner.MigrateAsync(TestToken);

        // The contract script's NOT NULL only succeeds because the code step ran between expand and contract.
        applied.Select(a => a.Step.Version).Should().Equal("20260901000100", Backfill, "20260901000300", null);
        applied.Select(a => a.Outcome).Should().OnlyContain(o => o == MigrationOutcome.Applied);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM plan_item_names WHERE name_upper IN ('A', 'B')"))
            .Should().Be(2L);

        // One history, in execution order, with the step kind recorded.
        (await StringsAsync(connectionString, "SELECT kind || ':' || coalesce(version, '-') FROM elarion_schema_history ORDER BY installed_rank"))
            .Should().Equal("sql:20260901000100", $"code:{Backfill}", "sql:20260901000300", "sql:-");

        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        (await runner.ValidateAsync(TestToken)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_CodeStepFailure_RollsBackItsWritesAndHistoryRow_ThenRetriesOnNextRun() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var attempts = 0;
        var step = new DelegateCodeMigration(Backfill, "write then maybe fail", async (context, ct) => {
            await context.ExecuteSqlAsync("INSERT INTO plan_log (note) VALUES ('code')", ct);
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("boom");
        });
        var runner = CreateRunner(connectionString, "CodeRollback.", step);

        var act = () => runner.MigrateAsync(TestToken);
        var failure = await act.Should().ThrowAsync<MigrationExecutionException>();
        failure.Which.StepName.Should().Be(nameof(DelegateCodeMigration));
        failure.Which.Message.Should().Contain("rolled back").And.Contain("boom");

        // The code step's insert rolled back with its transaction, nothing was recorded for it, and the plan
        // stopped before the later script.
        (await ScalarAsync(connectionString, "SELECT count(*) FROM plan_log")).Should().Be(0L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history")).Should().Be(1L);

        // Retry on the next start: the code step reruns and the plan completes.
        var applied = await runner.MigrateAsync(TestToken);
        applied.Select(a => a.Step.Kind).Should().Equal("code", "sql");
        (await StringsAsync(connectionString, "SELECT note FROM plan_log ORDER BY note")).Should().Equal("after", "code");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Migrate_NonTransactionalCodeStepFailure_RecordsNothingAndRetries() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var attempts = 0;
        var step = new DelegateCodeMigration(Backfill, "batch that commits as it goes", async (context, ct) => {
            Assert.Null(context.Transaction);
            await context.ExecuteSqlAsync("INSERT INTO plan_log (note) SELECT 'batch' WHERE NOT EXISTS (SELECT 1 FROM plan_log WHERE note = 'batch')", ct);
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("crashed mid-batch");
        }) { UseTransaction = false };
        var runner = CreateRunner(connectionString, "CodeRollback.", step);

        var act = () => runner.MigrateAsync(TestToken);
        (await act.Should().ThrowAsync<MigrationExecutionException>()).Which.Message
            .Should().Contain("nothing was recorded").And.Contain("idempotent");

        // The partial write stuck (no transaction) but no failed row blocks the plan: it simply retries.
        (await ScalarAsync(connectionString, "SELECT count(*) FROM plan_log")).Should().Be(1L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history WHERE outcome = 'failed'"))
            .Should().Be(0L);

        (await runner.MigrateAsync(TestToken)).Should().HaveCount(2);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM plan_log WHERE note = 'batch'")).Should().Be(1L);
    }

    [Fact]
    public async Task ConcurrentRunners_RunACodeStepExactlyOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var executions = 0;
        var step = new DelegateCodeMigration(Backfill, "count executions", async (context, ct) => {
            Interlocked.Increment(ref executions);
            await Task.Delay(300, ct);
            await context.ExecuteSqlAsync("UPDATE plan_items SET name_upper = upper(name)", ct);
        });
        var first = CreateRunner(connectionString, "Interleaved.", step);
        var second = CreateRunner(connectionString, "Interleaved.", step);

        var results = await Task.WhenAll(first.MigrateAsync(TestToken), second.MigrateAsync(TestToken));

        results.SelectMany(r => r).Count(r => r.Step.Kind == MigrationStepKinds.Code).Should().Be(1);
        executions.Should().Be(1);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history")).Should().Be(4L);
    }

    [Fact]
    public async Task Migrate_StepAlreadySatisfied_IsRecordedWithoutRunning() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var executed = false;
        var step = new DelegateCodeMigration(Backfill, "fresh install has nothing to convert", (_, _) => {
            executed = true;
            return Task.CompletedTask;
        }) { Satisfied = (_, _) => ValueTask.FromResult(true) };
        var runner = CreateRunner(connectionString, "CodeRollback.", step);

        var applied = await runner.MigrateAsync(TestToken);

        executed.Should().BeFalse();
        applied.Select(a => a.Outcome).Should().Equal(
            MigrationOutcome.Applied, MigrationOutcome.Satisfied, MigrationOutcome.Applied);
        (await ScalarAsync(connectionString, "SELECT outcome FROM elarion_schema_history WHERE kind = 'code'"))
            .Should().Be("satisfied");
        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Baseline_SkipsScriptsAndCodeStepsAtOrBelowTheBaselineVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var executed = false;
        var step = new DelegateCodeMigration(Backfill, "already converted by hand", (_, _) => {
            executed = true;
            return Task.CompletedTask;
        });
        var runner = CreateRunner(connectionString, "CodeRollback.", step);

        await runner.BaselineAsync(Backfill, cancellationToken: TestToken);
        await ScalarAsync(connectionString, "CREATE TABLE plan_log (note text NOT NULL); SELECT 1");
        var applied = await runner.MigrateAsync(TestToken);

        executed.Should().BeFalse();
        applied.Should().ContainSingle().Which.Step.Version.Should().Be("20260901000300");
        (await ScalarAsync(connectionString, "SELECT kind FROM elarion_schema_history WHERE installed_rank = 1"))
            .Should().Be("baseline");
    }

    [Fact]
    public async Task Migrate_CodeStepSharingAScriptVersion_FailsValidationNamingBoth() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var clash = new DelegateCodeMigration("20260901000100", "clashes with the expand script",
            (_, _) => Task.CompletedTask);
        var runner = CreateRunner(connectionString, "Interleaved.", clash);

        var act = () => runner.MigrateAsync(TestToken);

        (await act.Should().ThrowAsync<MigrationException>()).Which.Message
            .Should().Contain("Duplicate migration version 20260901000100")
            .And.Contain("V20260901000100__expand.sql")
            .And.Contain(nameof(DelegateCodeMigration));
    }

    [Fact]
    public async Task Migrate_UpgradesAScriptShapedHistoryTableInPlace() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var runner = CreateRunner(connectionString, "Basic.");
        await runner.MigrateAsync(TestToken);

        // Rewrite the history into the layout the script-only runner wrote (ADR-0057/0060).
        await ScalarAsync(connectionString, """
            ALTER TABLE elarion_schema_history DROP COLUMN kind;
            ALTER TABLE elarion_schema_history RENAME COLUMN step_name TO script_name;
            ALTER TABLE elarion_schema_history RENAME COLUMN outcome TO state;
            SELECT 1
            """);

        // A read-only session understands the old layout without touching it...
        (await runner.GetPendingAsync(TestToken)).Should().BeEmpty();
        (await runner.ValidateAsync(TestToken)).IsValid.Should().BeTrue();
        (await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'elarion_schema_history' AND column_name = 'script_name'"))
            .Should().Be(1L);

        // ...and the next exclusive run upgrades it in place: rows keep their meaning and become sql steps.
        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        (await StringsAsync(connectionString, "SELECT kind || ':' || outcome || ':' || step_name FROM elarion_schema_history ORDER BY installed_rank"))
            .Should().Equal(
                "sql:applied:V1__create_customers.sql",
                "sql:applied:V2__add_email.sql",
                "sql:applied:R__customer_view.sql");
    }

    private static IMigrationRunner CreateRunner(string connectionString, string scenario,
        params ICodeMigration[] codeMigrations) {
        var options = new MigrationOptions();
        options.AddScripts(typeof(MigrationPlanIntegrationTests).Assembly,
            MigrationScriptDiscoveryTests.ScriptPrefix + scenario);
        if (codeMigrations.Length > 0) options.AddCodeMigrations(codeMigrations);

        return new PostgreSqlMigrationRunner(connectionString, options);
    }

    private static DelegateCodeMigration BackfillStep() {
        return new DelegateCodeMigration(Backfill, "backfill name_upper",
            (context, ct) => context.ExecuteSqlAsync("UPDATE plan_items SET name_upper = upper(name)", ct));
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
}

/// <summary>A code migration whose behavior the test supplies as delegates.</summary>
internal sealed class DelegateCodeMigration(
    string version,
    string description,
    Func<MigrationStepContext, CancellationToken, Task> execute) : ICodeMigration {
    public string Version => version;

    public string Description => description;

    public bool UseTransaction { get; init; } = true;

    public Func<MigrationStepContext, CancellationToken, ValueTask<bool>>? Satisfied { get; init; }

    public ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        return Satisfied?.Invoke(context, cancellationToken) ?? ValueTask.FromResult(false);
    }

    public Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        return execute(context, cancellationToken);
    }
}
