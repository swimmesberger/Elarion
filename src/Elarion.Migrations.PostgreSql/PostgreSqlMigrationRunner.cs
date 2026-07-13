using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// The PostgreSQL implementation of <see cref="IMigrationRunner"/> (ADR-0057). One dedicated connection,
/// single-threaded, serialized across nodes by a session-level <c>pg_advisory_lock</c> — session scope
/// means a crashed runner releases the lock with its connection, and a transaction-scoped lock would hang
/// <c>CREATE INDEX CONCURRENTLY</c> scripts. Each versioned script commits its history row in its own
/// transaction, so a failed transactional migration leaves no trace and needs no repair.
/// </summary>
public sealed class PostgreSqlMigrationRunner : IMigrationRunner {
    private readonly string? _connectionString;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly PostgreSqlMigrationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a runner that opens its dedicated connection from a connection string.</summary>
    public PostgreSqlMigrationRunner(string connectionString, PostgreSqlMigrationOptions options, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : this(options, logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // A genuinely dedicated physical connection: closing it always releases the session advisory
        // lock server-side, so no error path can park a pooled connector that still holds the lock.
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
    }

    /// <summary>Creates a runner that borrows connections from an existing data source (never disposes it).</summary>
    public PostgreSqlMigrationRunner(NpgsqlDataSource dataSource, PostgreSqlMigrationOptions options, ILogger<PostgreSqlMigrationRunner>? logger = null)
        : this(options, logger) {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    private PostgreSqlMigrationRunner(PostgreSqlMigrationOptions options, ILogger<PostgreSqlMigrationRunner>? logger) {
        ArgumentNullException.ThrowIfNull(options);
        SchemaHistory.ValidateTableName(options.HistoryTableName);
        _options = options;
        _logger = logger ?? NullLogger<PostgreSqlMigrationRunner>.Instance;
    }

    private int CommandTimeoutSeconds => ToSeconds(_options.CommandTimeout);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationScriptInfo>> MigrateAsync(CancellationToken cancellationToken = default) {
        var scripts = DiscoverOrThrow();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);
        try {
            var history = CreateHistory(connection);
            await history.EnsureTableAsync(cancellationToken);
            var rows = await history.LoadAsync(cancellationToken);
            var plan = MigrationPlanner.Build(scripts, rows);

            ThrowIfBlocked(plan);

            var nextRank = rows.Count == 0 ? 1 : rows.Max(r => r.InstalledRank) + 1;
            var applied = new List<MigrationScriptInfo>();

            foreach (var script in plan.PendingVersioned) {
                if (plan.OutOfOrder.Contains(script)) {
                    _logger.LogWarning(
                        "Applying migration {ScriptName} out of order: version {Version} is below an already-applied version.",
                        script.ScriptName, script.Version!.Text);
                }

                await ApplyAsync(connection, history, script, nextRank++, cancellationToken);
                applied.Add(script.ToInfo());
            }

            foreach (var script in plan.PendingRepeatable) {
                await ApplyAsync(connection, history, script, nextRank++, cancellationToken);
                applied.Add(script.ToInfo());
            }

            if (applied.Count == 0) {
                _logger.LogInformation("Schema is up to date; no migrations to apply.");
            }
            else {
                _logger.LogInformation("Applied {Count} migration(s).", applied.Count);
            }

            return applied;
        }
        finally {
            await ReleaseLockAsync(connection);
        }
    }

    /// <inheritdoc />
    public async Task<MigrationValidationResult> ValidateAsync(CancellationToken cancellationToken = default) {
        var scripts = MigrationScriptDiscovery.Discover(_options.ScriptSources);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var history = CreateHistory(connection);
        List<AppliedMigrationRow> rows = await history.TableExistsAsync(cancellationToken)
            ? await history.LoadAsync(cancellationToken)
            : [];

        var plan = MigrationPlanner.Build(scripts, rows);
        var errors = new List<MigrationValidationError>();
        errors.AddRange(scripts.Errors);
        errors.AddRange(plan.Errors);
        foreach (var row in plan.FailedRows) {
            errors.Add(new MigrationValidationError {
                ScriptName = row.ScriptName,
                Message = FailedRowMessage(row),
            });
        }

        if (_options.OutOfOrder == OutOfOrderPolicy.Deny) {
            foreach (var script in plan.OutOfOrder) {
                errors.Add(new MigrationValidationError {
                    ScriptName = script.ScriptName,
                    Message = OutOfOrderMessage(script),
                });
            }
        }

        return new MigrationValidationResult {
            Errors = errors,
            Pending = plan.PendingVersioned.Concat(plan.PendingRepeatable).Select(s => s.ToInfo()).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationScriptInfo>> GetPendingAsync(CancellationToken cancellationToken = default) {
        var scripts = DiscoverOrThrow();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var history = CreateHistory(connection);
        List<AppliedMigrationRow> rows = await history.TableExistsAsync(cancellationToken)
            ? await history.LoadAsync(cancellationToken)
            : [];

        var plan = MigrationPlanner.Build(scripts, rows);
        return plan.PendingVersioned.Concat(plan.PendingRepeatable).Select(s => s.ToInfo()).ToList();
    }

    /// <inheritdoc />
    public async Task BaselineAsync(string version, string? description = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed)) {
            throw new MigrationException($"Baseline version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);
        try {
            var history = CreateHistory(connection);
            await history.EnsureTableAsync(cancellationToken);
            var rows = await history.LoadAsync(cancellationToken);
            if (rows.Count > 0) {
                throw new MigrationException(
                    $"Cannot baseline at version {parsed.Text}: the history table already has {rows.Count} row(s). "
                    + "Baselining is only for adopting an existing database before its first migration run.");
            }

            await history.InsertAsync(
                installedRank: 1,
                version: parsed.Text,
                description: description ?? "baseline",
                scriptName: $"baseline {parsed.Text}",
                checksum: null,
                state: MigrationStates.Baseline,
                durationMs: 0,
                transaction: null,
                cancellationToken);
            _logger.LogInformation("Baselined schema history at version {Version}.", parsed.Text);
        }
        finally {
            await ReleaseLockAsync(connection);
        }
    }

    /// <inheritdoc />
    public async Task ResolveFailedAsync(string version, ResolveAction action, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed)) {
            throw new MigrationException($"Version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");
        }

        var scripts = MigrationScriptDiscovery.Discover(_options.ScriptSources);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);
        try {
            var history = CreateHistory(connection);
            await history.EnsureTableAsync(cancellationToken);
            var rows = await history.LoadAsync(cancellationToken);
            var failed = rows.FirstOrDefault(row =>
                row.State == MigrationStates.Failed
                && row.Version is not null
                && MigrationVersion.TryParse(row.Version, out var rowVersion)
                && rowVersion.Equals(parsed));
            if (failed is null) {
                throw new MigrationException($"No failed migration with version {parsed.Text} exists in the history.");
            }

            if (action == ResolveAction.Retry) {
                await history.DeleteAsync(failed.InstalledRank, cancellationToken);
                _logger.LogInformation(
                    "Resolved failed migration {ScriptName} (version {Version}) as Retry; the next migrate reruns it.",
                    failed.ScriptName, parsed.Text);
            }
            else {
                var script = scripts.Versioned.FirstOrDefault(s => s.Version!.Equals(parsed));
                await history.MarkAppliedAsync(failed.InstalledRank, script?.Checksum, cancellationToken);
                _logger.LogInformation(
                    "Resolved failed migration {ScriptName} (version {Version}) as MarkApplied.",
                    failed.ScriptName, parsed.Text);
            }
        }
        finally {
            await ReleaseLockAsync(connection);
        }
    }

    private MigrationScriptSet DiscoverOrThrow() {
        var scripts = MigrationScriptDiscovery.Discover(_options.ScriptSources);
        if (scripts.Errors.Count > 0) {
            throw new MigrationException(
                "Migration script validation failed:\n" + string.Join("\n", scripts.Errors.Select(e => "- " + e.Message)));
        }

        return scripts;
    }

    private void ThrowIfBlocked(MigrationPlan plan) {
        if (plan.FailedRows.Count > 0) {
            var row = plan.FailedRows[0];
            throw new MigrationFailedStateException(row.Version ?? "", row.ScriptName, FailedRowMessage(row));
        }

        if (plan.Errors.Count > 0) {
            throw new MigrationException(
                "Migration validation failed:\n" + string.Join("\n", plan.Errors.Select(e => "- " + e.Message)));
        }

        if (_options.OutOfOrder == OutOfOrderPolicy.Deny && plan.OutOfOrder.Count > 0) {
            throw new MigrationException(
                "Out-of-order migrations denied (OutOfOrderPolicy.Deny):\n"
                + string.Join("\n", plan.OutOfOrder.Select(s => "- " + OutOfOrderMessage(s))));
        }
    }

    private async Task ApplyAsync(
        NpgsqlConnection connection,
        SchemaHistory history,
        MigrationScript script,
        int installedRank,
        CancellationToken cancellationToken) {
        _logger.LogInformation("Applying migration {ScriptName}…", script.ScriptName);
        var stopwatch = Stopwatch.StartNew();

        if (script.NoTransaction) {
            await ApplyWithoutTransactionAsync(connection, history, script, installedRank, stopwatch, cancellationToken);
        }
        else {
            await ApplyTransactionalAsync(connection, history, script, installedRank, stopwatch, cancellationToken);
        }

        _logger.LogInformation("Applied migration {ScriptName} in {DurationMs} ms.", script.ScriptName, stopwatch.ElapsedMilliseconds);
    }

    private async Task ApplyTransactionalAsync(
        NpgsqlConnection connection,
        SchemaHistory history,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try {
            await using (var command = new NpgsqlCommand(script.Sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds }) {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // The history row commits atomically with the script — the no-repair invariant.
            await history.InsertAsync(
                installedRank,
                script.Version?.Text,
                script.Description,
                script.ScriptName,
                script.Checksum,
                MigrationStates.Applied,
                stopwatch.ElapsedMilliseconds,
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not MigrationException and not OperationCanceledException) {
            throw new MigrationExecutionException(
                script.ScriptName,
                $"Migration '{script.ScriptName}' failed and was rolled back; no history row was recorded. "
                + $"Fix the script and rerun. Cause: {ex.Message}",
                ex);
        }
    }

    private async Task ApplyWithoutTransactionAsync(
        NpgsqlConnection connection,
        SchemaHistory history,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        // Statement by statement: a multi-statement command travels as one implicit transaction, which
        // would break the CREATE INDEX CONCURRENTLY case this directive exists for.
        var statements = SqlStatementSplitter.Split(script.Sql);
        try {
            foreach (var statement in statements) {
                await using var command = new NpgsqlCommand(statement, connection) { CommandTimeout = CommandTimeoutSeconds };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            if (script.IsRepeatable) {
                // Repeatables are idempotent by doctrine and have no recorded checksum for this content
                // yet, so the next run simply retries; no failed row, no limbo.
                throw new MigrationExecutionException(
                    script.ScriptName,
                    $"Repeatable migration '{script.ScriptName}' failed (no-transaction). Fix the script and rerun. Cause: {ex.Message}",
                    ex);
            }

            await RecordFailureAsync(history, script, installedRank, stopwatch, cancellationToken);
            throw new MigrationExecutionException(
                script.ScriptName,
                $"Migration '{script.ScriptName}' failed while running outside a transaction and may be half-applied; "
                + $"a failed history row was recorded and subsequent runs fail closed. Resolve it with "
                + $"IMigrationRunner.ResolveFailedAsync(\"{script.Version!.Text}\", ResolveAction.Retry | MarkApplied). Cause: {ex.Message}",
                ex);
        }

        await history.InsertAsync(
            installedRank,
            script.Version?.Text,
            script.Description,
            script.ScriptName,
            script.Checksum,
            MigrationStates.Applied,
            stopwatch.ElapsedMilliseconds,
            transaction: null,
            cancellationToken);
    }

    private async Task RecordFailureAsync(
        SchemaHistory history,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        try {
            await history.InsertAsync(
                installedRank,
                script.Version!.Text,
                script.Description,
                script.ScriptName,
                script.Checksum,
                MigrationStates.Failed,
                stopwatch.ElapsedMilliseconds,
                transaction: null,
                cancellationToken);
        }
        catch (Exception recordEx) {
            // The connection may be gone entirely; the original failure is the one worth surfacing.
            _logger.LogError(recordEx, "Failed to record the failed history row for migration {ScriptName}.", script.ScriptName);
        }
    }

    private static string FailedRowMessage(AppliedMigrationRow row) =>
        $"Migration '{row.ScriptName}' (version {row.Version}) previously failed while running outside a transaction "
        + $"and may be half-applied. Resolve it with IMigrationRunner.ResolveFailedAsync(\"{row.Version}\", "
        + "ResolveAction.Retry | MarkApplied) before migrating again.";

    private static string OutOfOrderMessage(MigrationScript script) =>
        $"Migration '{script.ScriptName}' (version {script.Version!.Text}) is versioned below an already-applied migration.";

    private SchemaHistory CreateHistory(NpgsqlConnection connection) =>
        new(connection, _options.HistoryTableName, CommandTimeoutSeconds);

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) {
        if (_dataSource is not null) {
            return await _dataSource.OpenConnectionAsync(cancellationToken);
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken) {
        // Session-level, never transaction-level: the lock must span non-transactional scripts, and it
        // dies with the connection — no lock row to clean up after a crash.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection) {
            CommandTimeout = ToSeconds(_options.LockTimeout),
            Parameters = { new NpgsqlParameter<long> { TypedValue = _options.AdvisoryLockKey } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection) {
        // Deliberately NOT the caller's token: a cancelled startup must still unlock, because a pooled
        // connection (the data-source overload) returned with the session lock held would block every
        // later runner until the pool prunes it. The unlock itself is instantaneous.
        try {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection) {
                CommandTimeout = ToSeconds(_options.LockTimeout),
                Parameters = { new NpgsqlParameter<long> { TypedValue = _options.AdvisoryLockKey } },
            };
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) {
            // A broken connection releases the session lock by itself.
            _logger.LogDebug(ex, "Releasing the migration advisory lock failed; the connection will release it.");
        }
    }

    /// <summary>Npgsql command timeouts are integer seconds with 0 as "no timeout"; non-positive spans (e.g. <see cref="Timeout.InfiniteTimeSpan"/>) mean no timeout.</summary>
    private static int ToSeconds(TimeSpan? timeout) =>
        timeout is null || timeout.Value <= TimeSpan.Zero ? 0 : Math.Max(1, (int)timeout.Value.TotalSeconds);
}
