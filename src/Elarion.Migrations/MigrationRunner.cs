using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Migrations;

/// <summary>
/// The database-neutral <see cref="IMigrationRunner"/> (ADR-0060): discovers and plans scripts and owns
/// the roll-forward execution policy, driving a database <see cref="IMigrationDatabase"/> provider for
/// the locking, history-table SQL, and script execution. Each versioned script's history row commits in
/// the script's own transaction, so a failed transactional migration leaves no trace and needs no repair.
/// <para>
/// This class is the extension point for provider convenience façades (e.g.
/// <c>PostgreSqlMigrationRunner</c>): a subclass wires a provider-specific <see cref="IMigrationDatabase"/>
/// and its options into the base constructor. Construct it directly with any provider otherwise.
/// </para>
/// </summary>
public class MigrationRunner : IMigrationRunner {
    private readonly IMigrationDatabase _database;
    private readonly MigrationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates a runner over the given database provider and options.</summary>
    /// <param name="database">The database-specific provider supplying locking, history SQL, and execution.</param>
    /// <param name="options">The migration options (script sources, out-of-order policy, timeouts).</param>
    /// <param name="logger">Optional logger; a null logger is used when omitted.</param>
    public MigrationRunner(IMigrationDatabase database, MigrationOptions options, ILogger? logger = null) {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        _database = database;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationScriptInfo>> MigrateAsync(CancellationToken cancellationToken = default) {
        var scripts = DiscoverOrThrow();

        await using var session = await _database.ConnectAsync(exclusive: true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        var rows = await session.LoadHistoryAsync(cancellationToken);
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

            await ApplyAsync(session, script, nextRank++, cancellationToken);
            applied.Add(script.ToInfo());
        }

        foreach (var script in plan.PendingRepeatable) {
            await ApplyAsync(session, script, nextRank++, cancellationToken);
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

    /// <inheritdoc />
    public async Task<MigrationValidationResult> ValidateAsync(CancellationToken cancellationToken = default) {
        var scripts = MigrationScriptDiscovery.Discover(_options.ScriptSources);

        await using var session = await _database.ConnectAsync(exclusive: false, cancellationToken);
        var rows = await LoadIfExistsAsync(session, cancellationToken);

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

        await using var session = await _database.ConnectAsync(exclusive: false, cancellationToken);
        var rows = await LoadIfExistsAsync(session, cancellationToken);

        var plan = MigrationPlanner.Build(scripts, rows);
        return plan.PendingVersioned.Concat(plan.PendingRepeatable).Select(s => s.ToInfo()).ToList();
    }

    /// <inheritdoc />
    public async Task BaselineAsync(string version, string? description = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed)) {
            throw new MigrationException($"Baseline version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");
        }

        await using var session = await _database.ConnectAsync(exclusive: true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        var rows = await session.LoadHistoryAsync(cancellationToken);
        if (rows.Count > 0) {
            throw new MigrationException(
                $"Cannot baseline at version {parsed.Text}: the history table already has {rows.Count} row(s). "
                + "Baselining is only for adopting an existing database before its first migration run.");
        }

        await session.InsertHistoryRowAsync(
            new MigrationHistoryRecord {
                InstalledRank = 1,
                Version = parsed.Text,
                Description = description ?? "baseline",
                ScriptName = $"baseline {parsed.Text}",
                Checksum = null,
                State = MigrationStates.Baseline,
                DurationMs = 0,
            },
            cancellationToken);
        _logger.LogInformation("Baselined schema history at version {Version}.", parsed.Text);
    }

    /// <inheritdoc />
    public async Task ResolveFailedAsync(string version, ResolveAction action, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed)) {
            throw new MigrationException($"Version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");
        }

        var scripts = MigrationScriptDiscovery.Discover(_options.ScriptSources);

        await using var session = await _database.ConnectAsync(exclusive: true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        var rows = await session.LoadHistoryAsync(cancellationToken);
        var failed = rows.FirstOrDefault(row =>
            row.State == MigrationStates.Failed
            && row.Version is not null
            && MigrationVersion.TryParse(row.Version, out var rowVersion)
            && rowVersion.Equals(parsed));
        if (failed is null) {
            throw new MigrationException($"No failed migration with version {parsed.Text} exists in the history.");
        }

        if (action == ResolveAction.Retry) {
            await session.DeleteHistoryRowAsync(failed.InstalledRank, cancellationToken);
            _logger.LogInformation(
                "Resolved failed migration {ScriptName} (version {Version}) as Retry; the next migrate reruns it.",
                failed.ScriptName, parsed.Text);
        }
        else {
            var script = scripts.Versioned.FirstOrDefault(s => s.Version!.Equals(parsed));
            await session.MarkHistoryRowAppliedAsync(failed.InstalledRank, script?.Checksum, cancellationToken);
            _logger.LogInformation(
                "Resolved failed migration {ScriptName} (version {Version}) as MarkApplied.",
                failed.ScriptName, parsed.Text);
        }
    }

    private static async Task<IReadOnlyList<AppliedMigrationRow>> LoadIfExistsAsync(IMigrationSession session, CancellationToken cancellationToken) =>
        await session.HistoryTableExistsAsync(cancellationToken)
            ? await session.LoadHistoryAsync(cancellationToken)
            : [];

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

    private async Task ApplyAsync(IMigrationSession session, MigrationScript script, int installedRank, CancellationToken cancellationToken) {
        _logger.LogInformation("Applying migration {ScriptName}…", script.ScriptName);
        var stopwatch = Stopwatch.StartNew();

        if (script.NoTransaction) {
            await ApplyWithoutTransactionAsync(session, script, installedRank, stopwatch, cancellationToken);
        }
        else {
            await ApplyTransactionalAsync(session, script, installedRank, stopwatch, cancellationToken);
        }

        _logger.LogInformation("Applied migration {ScriptName} in {DurationMs} ms.", script.ScriptName, stopwatch.ElapsedMilliseconds);
    }

    private async Task ApplyTransactionalAsync(
        IMigrationSession session,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        try {
            // The factory runs just before the in-transaction insert, so the row carries the measured
            // execution time; the history row commits atomically with the script — the no-repair invariant.
            await session.ExecuteInTransactionAsync(
                script.Sql,
                () => AppliedRow(script, installedRank, stopwatch.ElapsedMilliseconds),
                cancellationToken);
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
        IMigrationSession session,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        try {
            await session.ExecuteWithoutTransactionAsync(script.Sql, cancellationToken);
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

            await RecordFailureAsync(session, script, installedRank, stopwatch, cancellationToken);
            throw new MigrationExecutionException(
                script.ScriptName,
                $"Migration '{script.ScriptName}' failed while running outside a transaction and may be half-applied; "
                + $"a failed history row was recorded and subsequent runs fail closed. Resolve it with "
                + $"IMigrationRunner.ResolveFailedAsync(\"{script.Version!.Text}\", ResolveAction.Retry | MarkApplied). Cause: {ex.Message}",
                ex);
        }

        await session.InsertHistoryRowAsync(AppliedRow(script, installedRank, stopwatch.ElapsedMilliseconds), cancellationToken);
    }

    private async Task RecordFailureAsync(
        IMigrationSession session,
        MigrationScript script,
        int installedRank,
        Stopwatch stopwatch,
        CancellationToken cancellationToken) {
        try {
            await session.InsertHistoryRowAsync(
                new MigrationHistoryRecord {
                    InstalledRank = installedRank,
                    Version = script.Version!.Text,
                    Description = script.Description,
                    ScriptName = script.ScriptName,
                    Checksum = script.Checksum,
                    State = MigrationStates.Failed,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                },
                cancellationToken);
        }
        catch (Exception recordEx) {
            // The connection may be gone entirely; the original failure is the one worth surfacing.
            _logger.LogError(recordEx, "Failed to record the failed history row for migration {ScriptName}.", script.ScriptName);
        }
    }

    private static MigrationHistoryRecord AppliedRow(MigrationScript script, int installedRank, long durationMs) =>
        new() {
            InstalledRank = installedRank,
            Version = script.Version?.Text,
            Description = script.Description,
            ScriptName = script.ScriptName,
            Checksum = script.Checksum,
            State = MigrationStates.Applied,
            DurationMs = durationMs,
        };

    private static string FailedRowMessage(AppliedMigrationRow row) =>
        $"Migration '{row.ScriptName}' (version {row.Version}) previously failed while running outside a transaction "
        + $"and may be half-applied. Resolve it with IMigrationRunner.ResolveFailedAsync(\"{row.Version}\", "
        + "ResolveAction.Retry | MarkApplied) before migrating again.";

    private static string OutOfOrderMessage(MigrationScript script) =>
        $"Migration '{script.ScriptName}' (version {script.Version!.Text}) is versioned below an already-applied migration.";
}
