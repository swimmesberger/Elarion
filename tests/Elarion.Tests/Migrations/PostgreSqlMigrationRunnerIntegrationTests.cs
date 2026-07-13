using AwesomeAssertions;
using Elarion.Migrations.PostgreSql;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// The ADR-0057 execution model against real PostgreSQL: per-script transactions with the history row
/// committed alongside (no repair, no failed-row limbo for transactional scripts), the no-transaction
/// directive with its explicit failed state and <see cref="IMigrationRunner.ResolveFailedAsync"/>
/// recovery, out-of-order policy, baselining, and advisory-lock serialization of concurrent runners.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlMigrationRunnerIntegrationTests(PostgreSqlMigrationsFixture fixture)
    : IClassFixture<PostgreSqlMigrationsFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_AppliesVersionedThenRepeatable_AndSecondRunIsNoOp() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var runner = CreateRunner(connectionString, "Basic.");

        var pendingBefore = await runner.GetPendingAsync(TestToken);
        pendingBefore.Select(p => p.ScriptName).Should().Equal(
            "V1__create_customers.sql", "V2__add_email.sql", "R__customer_view.sql");

        var applied = await runner.MigrateAsync(TestToken);
        applied.Select(a => a.Version).Should().Equal("1", "2", null);

        // The schema is really there: the view selects the column V2 added the table for.
        await using (var connection = new NpgsqlConnection(connectionString)) {
            await connection.OpenAsync(TestToken);
            await using var command = new NpgsqlCommand(
                "INSERT INTO mig_customers (id, name, email) VALUES (gen_random_uuid(), 'a', 'a@example.org'); SELECT count(*) FROM mig_customer_names;",
                connection);
            (await command.ExecuteScalarAsync(TestToken)).Should().Be(1L);
        }

        (await runner.MigrateAsync(TestToken)).Should().BeEmpty();
        var validation = await runner.ValidateAsync(TestToken);
        validation.IsValid.Should().BeTrue();
        validation.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_RepeatableWithChangedChecksum_RerunsOnlyThatScript() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        await CreateRunner(connectionString, "Basic.").MigrateAsync(TestToken);

        // Same versioned scripts, changed view body: only the repeatable reruns.
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
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
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
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

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
    public async Task Migrate_NoTransactionDirective_RunsCreateIndexConcurrently() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

        await CreateRunner(connectionString, "NoTx.").MigrateAsync(TestToken);

        (await ScalarAsync(
            connectionString,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'mig_events_val_idx'")).Should().Be(1L);
    }

    [Fact]
    public async Task Migrate_NoTransactionFailure_FailsClosedUntilResolvedRetry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

        var broken = CreateRunner(connectionString, "NoTxFail.");
        var act = () => broken.MigrateAsync(TestToken);
        (await act.Should().ThrowAsync<MigrationExecutionException>())
            .Which.Message.Should().Contain("ResolveFailedAsync");

        // The partial effect stuck (no transaction) and an explicit failed row was recorded.
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
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'mig_points' AND column_name = 'extra'")).Should().Be(1L);
    }

    [Fact]
    public async Task ResolveFailed_MarkApplied_DeclaresTheVersionDone() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);

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
    public async Task ResolveFailed_WithoutFailedRow_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var runner = CreateRunner(connectionString, "Basic.");
        await runner.MigrateAsync(TestToken);

        var act = () => runner.ResolveFailedAsync("1", ResolveAction.Retry, TestToken);
        (await act.Should().ThrowAsync<MigrationException>()).Which.Message.Should().Contain("No failed migration");
    }

    [Fact]
    public async Task Migrate_OutOfOrderWarn_AppliesAndRecordsTrueExecutionOrder() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        await CreateRunner(connectionString, "OutOfOrderA.").MigrateAsync(TestToken);

        var applied = await CreateRunner(connectionString, "OutOfOrderB.").MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.Version.Should().Be("2");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestToken);
        await using var command = new NpgsqlCommand(
            "SELECT version FROM elarion_schema_history ORDER BY installed_rank", connection);
        await using var reader = await command.ExecuteReaderAsync(TestToken);
        var versions = new List<string>();
        while (await reader.ReadAsync(TestToken)) {
            versions.Add(reader.GetString(0));
        }

        versions.Should().Equal("1", "3", "2");
    }

    [Fact]
    public async Task Migrate_OutOfOrderDeny_FailsNamingTheScript() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        await CreateRunner(connectionString, "OutOfOrderA.").MigrateAsync(TestToken);

        var denying = CreateRunner(connectionString, "OutOfOrderB.", o => o.OutOfOrder = OutOfOrderPolicy.Deny);
        var act = () => denying.MigrateAsync(TestToken);
        (await act.Should().ThrowAsync<MigrationException>()).Which.Message.Should().Contain("V2__two.sql");

        var validation = await denying.ValidateAsync(TestToken);
        validation.Errors.Should().ContainSingle(e => e.ScriptName == "V2__two.sql");
    }

    [Fact]
    public async Task Baseline_SkipsVersionsAtOrBelow_AndRequiresEmptyHistory() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var runner = CreateRunner(connectionString, "Baseline.");

        await runner.BaselineAsync("1", cancellationToken: TestToken);
        var applied = await runner.MigrateAsync(TestToken);
        applied.Should().ContainSingle().Which.Version.Should().Be("2");

        (await ScalarAsync(connectionString, "SELECT to_regclass('mig_base_one') IS NULL")).Should().Be(true);
        (await ScalarAsync(connectionString, "SELECT to_regclass('mig_base_two') IS NOT NULL")).Should().Be(true);

        var again = () => runner.BaselineAsync("2", cancellationToken: TestToken);
        (await again.Should().ThrowAsync<MigrationException>()).Which.Message.Should().Contain("already has");
    }

    [Fact]
    public async Task ConcurrentRunners_SerializeOnTheAdvisoryLock() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var connectionString = await fixture.CreateDatabaseAsync(TestToken);
        var first = CreateRunner(connectionString, "Basic.");
        var second = CreateRunner(connectionString, "Basic.");

        var results = await Task.WhenAll(first.MigrateAsync(TestToken), second.MigrateAsync(TestToken));

        // The loser waited on the lock, re-read history, and applied nothing (or the remainder — never a duplicate).
        results.SelectMany(r => r).Should().HaveCount(3);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM elarion_schema_history")).Should().Be(3L);
        (await ScalarAsync(connectionString, "SELECT count(*) FROM mig_customers")).Should().Be(0L);
    }

    private static IMigrationRunner CreateRunner(
        string connectionString,
        string scenario,
        Action<PostgreSqlMigrationOptions>? configure = null) {
        var options = new PostgreSqlMigrationOptions().AddScripts(
            typeof(PostgreSqlMigrationRunnerIntegrationTests).Assembly,
            MigrationScriptDiscoveryTests.ScriptPrefix + scenario);
        configure?.Invoke(options);
        return new PostgreSqlMigrationRunner(connectionString, options);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestToken);
    }
}
