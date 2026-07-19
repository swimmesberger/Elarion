using AwesomeAssertions;
using Elarion.Migrations;
using Elarion.Migrations.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// The ADR-0060 execution model against real SQLite (in-process, no Docker): per-script transactions with
/// the history row committed alongside (no repair, no failed-row limbo for transactional scripts), the
/// no-transaction directive with its explicit failed state and
/// <see cref="IMigrationRunner.ResolveFailedAsync"/> recovery, checksum guarding, baselining, and
/// exclusive-lock serialization of concurrent runners.
/// </summary>
public sealed class SqliteMigrationRunnerIntegrationTests(SqliteMigrationsFixture fixture)
    : IClassFixture<SqliteMigrationsFixture> {
    internal const string ScriptPrefix = "Elarion.Tests.Migrations.SqliteScripts.";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_AppliesVersionedThenRepeatable_AndSecondRunIsNoOp() {
        var connectionString = fixture.CreateConnectionString();
        var runner = CreateRunner(connectionString, "Basic.");

        var pendingBefore = await runner.GetPendingAsync(TestToken);
        pendingBefore.Select(p => p.ScriptName).Should().Equal(
            "V1__create_customers.sql", "V2__add_email.sql", "R__customer_view.sql");

        var applied = await runner.MigrateAsync(TestToken);
        applied.Select(a => a.Version).Should().Equal("1", "2", null);

        // The schema is really there: the view selects the column V2 added the table for.
        await using (var connection = new SqliteConnection(connectionString)) {
            await connection.OpenAsync(TestToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO mig_customers (id, name, email) VALUES ('a', 'a', 'a@example.org'); SELECT count(*) FROM mig_customer_names;";
            (await command.ExecuteScalarAsync(TestToken)).Should().Be(1L);
        }

        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        var validation = await runner.ValidateAsync(TestToken);
        validation.IsValid.Should().BeTrue();
        validation.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_RepeatableWithChangedChecksum_RerunsOnlyThatScript() {
        var connectionString = fixture.CreateConnectionString();
        await CreateRunner(connectionString, "Basic.").MigrateAsync(TestToken);

        var changed = CreateRunner(connectionString, "RepeatableChanged.");
        var applied = await changed.MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.ScriptName.Should().Be("R__customer_view.sql");

        // The re-applied view now exposes the email column, and a new history row was appended.
        (await ScalarAsync(connectionString, "SELECT count(email) FROM mig_customer_names")).Should().Be(0L);
        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM elarion_schema_history WHERE script_name = 'R__customer_view.sql'")).Should().Be(2L);

        (await changed.MigrateAsync(TestToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_EditedAppliedScript_FailsNamingScriptAndBothChecksums() {
        var connectionString = fixture.CreateConnectionString();
        await CreateRunner(connectionString, "Basic.").MigrateAsync(TestToken);

        var edited = CreateRunner(connectionString, "BasicEdited.");
        var act = () => edited.MigrateAsync(TestToken);
        var failure = await act.Should().ThrowAsync<MigrationException>();
        failure.Which.Message.Should().Contain("V1__create_customers.sql").And.Contain("Checksum mismatch");

        var validation = await edited.ValidateAsync(TestToken);
        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().ContainSingle(e => e.ScriptName == "V1__create_customers.sql");
    }

    [Fact]
    public async Task Migrate_FailedTransactionalScript_RollsBackAndLeavesNoHistoryRow() {
        var connectionString = fixture.CreateConnectionString();

        var broken = CreateRunner(connectionString, "FailTx.");
        var act = () => broken.MigrateAsync(TestToken);
        (await act.Should().ThrowAsync<MigrationExecutionException>())
            .Which.ScriptName.Should().Be("V2__extend_things.sql");

        // V2's INSERT rolled back with its transaction, and no history row exists for it.
        (await ScalarAsync(connectionString, "SELECT count(*) FROM mig_things")).Should().Be(1L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history")).Should().Be(1L);

        // Fixing the script is the whole recovery — no resolve step, no repair.
        var applied = await CreateRunner(connectionString, "FailTxFixed.").MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.Version.Should().Be("2");
        (await ScalarAsync(connectionString, "SELECT count(*) FROM mig_things")).Should().Be(2L);
    }

    [Fact]
    public async Task Migrate_NoTransactionDirective_RunsInAutocommit() {
        var connectionString = fixture.CreateConnectionString();

        await CreateRunner(connectionString, "NoTx.").MigrateAsync(TestToken);

        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'mig_events_val_idx'")).Should().Be(1L);
    }

    [Fact]
    public async Task Migrate_NoTransactionFailure_FailsClosedUntilResolvedRetry() {
        var connectionString = fixture.CreateConnectionString();

        var broken = CreateRunner(connectionString, "NoTxFail.");
        var act = () => broken.MigrateAsync(TestToken);
        (await act.Should().ThrowAsync<MigrationExecutionException>())
            .Which.Message.Should().Contain("ResolveFailedAsync");

        // The first statement committed in autocommit (no transaction) and an explicit failed row was recorded.
        (await ScalarAsync(connectionString, "SELECT count(*) FROM mig_points")).Should().Be(1L);
        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM elarion_schema_history WHERE state = 'failed' AND version = '2'")).Should().Be(1L);

        // Every subsequent run fails closed, naming the recovery.
        var blocked = () => broken.MigrateAsync(TestToken);
        (await blocked.Should().ThrowAsync<MigrationFailedStateException>()).Which.Version.Should().Be("2");

        // Retry after fixing the script (idempotent against the partial state).
        var fixedRunner = CreateRunner(connectionString, "NoTxFailFixed.");
        await fixedRunner.ResolveFailedAsync("2", ResolveAction.Retry, TestToken);
        var applied = await fixedRunner.MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.Version.Should().Be("2");
        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM pragma_table_info('mig_points') WHERE name = 'extra'")).Should().Be(1L);
    }

    [Fact]
    public async Task ResolveFailed_MarkApplied_DeclaresTheVersionDone() {
        var connectionString = fixture.CreateConnectionString();

        var runner = CreateRunner(connectionString, "NoTxFail.");
        var act = () => runner.MigrateAsync(TestToken);
        await act.Should().ThrowAsync<MigrationExecutionException>();

        await runner.ResolveFailedAsync("2", ResolveAction.MarkApplied, TestToken);

        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        (await runner.ValidateAsync(TestToken)).IsValid.Should().BeTrue();
        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM elarion_schema_history WHERE state = 'applied' AND version = '2'")).Should().Be(1L);
    }

    [Fact]
    public async Task Baseline_SkipsVersionsAtOrBelow_AndRequiresEmptyHistory() {
        var connectionString = fixture.CreateConnectionString();
        var runner = CreateRunner(connectionString, "Baseline.");

        await runner.BaselineAsync("1", cancellationToken: TestToken);
        var applied = await runner.MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.Version.Should().Be("2");

        (await ScalarAsync(connectionString, "SELECT count(*) FROM sqlite_master WHERE name = 'mig_base_one'")).Should()
            .Be(0L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM sqlite_master WHERE name = 'mig_base_two'")).Should()
            .Be(1L);

        var again = () => runner.BaselineAsync("2", cancellationToken: TestToken);
        (await again.Should().ThrowAsync<MigrationException>()).Which.Message.Should().Contain("already has");
    }

    [Fact]
    public async Task ConcurrentRunners_SerializeOnTheExclusiveLock() {
        var connectionString = fixture.CreateConnectionString();
        var first = CreateRunner(connectionString, "Basic.");
        var second = CreateRunner(connectionString, "Basic.");

        var results = await Task.WhenAll(
            Task.Run(() => first.MigrateAsync(TestToken), TestToken),
            Task.Run(() => second.MigrateAsync(TestToken), TestToken));

        // The loser waited on the lock, re-read history, and applied nothing (or the remainder — never a duplicate).
        results.SelectMany(r => r).Should().HaveCount(3);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history")).Should().Be(3L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM mig_customers")).Should().Be(0L);
    }

    private static IMigrationRunner CreateRunner(
        string connectionString,
        string scenario,
        Action<MigrationOptions>? configure = null) {
        var options = new MigrationOptions();
        options.AddScripts(typeof(SqliteMigrationRunnerIntegrationTests).Assembly, ScriptPrefix + scenario);
        configure?.Invoke(options);
        return new SqliteMigrationRunner(connectionString, options);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql) {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestToken);
    }
}
