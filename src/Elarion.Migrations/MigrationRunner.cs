using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elarion.Migrations;

/// <summary>
/// The database-neutral <see cref="IMigrationRunner"/> (ADR-0060, ADR-0081): merges the steps of every source
/// into one version-ordered plan and owns the roll-forward execution policy, driving a database
/// <see cref="IMigrationDatabase"/> provider for the lock, the history-table SQL, and SQL execution. A
/// transactional step runs in a transaction on the plan's connection and its history row commits in that same
/// transaction, so a failed step leaves no trace and needs no repair.
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
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IMigrationStepSource> _sources;

    /// <summary>Creates a runner over the given database provider and options.</summary>
    /// <param name="database">The database-specific provider supplying locking, history SQL, and execution.</param>
    /// <param name="options">The migration options (step sources, out-of-order policy, timeouts).</param>
    /// <param name="logger">Optional logger; a null logger is used when omitted.</param>
    /// <param name="services">
    /// The provider steps resolve their scoped services from (a fresh scope per step); omitted, steps see an
    /// empty provider — fine for pure SQL plans.
    /// </param>
    /// <param name="additionalSources">Step sources beyond those carried by <paramref name="options"/> (the DI path adds the registered ones).</param>
    /// <exception cref="InvalidOperationException">No step source is configured at all.</exception>
    public MigrationRunner(IMigrationDatabase database, MigrationOptions options, ILogger? logger = null,
        IServiceProvider? services = null, IEnumerable<IMigrationStepSource>? additionalSources = null) {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        _database = database;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _services = services ?? EmptyServiceProvider.Instance;

        var sources = new List<IMigrationStepSource>();
        if (options.ScriptSources.Count > 0) sources.Add(new ScriptStepSource(options.ScriptSources));

        sources.AddRange(options.StepSources);
        if (additionalSources is not null) sources.AddRange(additionalSources);

        if (sources.Count == 0)
            throw new InvalidOperationException(
                "A migration runner requires at least one step source: options.AddScripts(assembly, resourceNamePrefix), a registered ICodeMigration, or an EF Core migration source.");

        _sources = sources;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationStepResult>> MigrateAsync(CancellationToken cancellationToken = default) {
        var steps = DiscoverOrThrow();

        await using var session = await _database.ConnectAsync(true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        // Read after the lock is held: an instance that waited behind another runner sees its records and no-ops.
        var rows = await session.LoadHistoryAsync(cancellationToken);
        var plan = MigrationPlanner.Build(steps, rows);

        ThrowIfBlocked(plan);

        var nextRank = rows.Count == 0 ? 1 : rows.Max(r => r.InstalledRank) + 1;
        var applied = new List<MigrationStepResult>();

        foreach (var planned in plan.PendingVersioned.Concat(plan.PendingRepeatable)) {
            if (plan.OutOfOrder.Contains(planned))
                _logger.LogWarning(
                    "Applying migration {StepName} out of order: version {Version} is below an already-applied version.",
                    planned.Step.Name, planned.Version!.Text);

            applied.Add(await ApplyAsync(session, planned, nextRank++, cancellationToken));
        }

        if (applied.Count == 0)
            _logger.LogInformation("Schema is up to date; no migrations to apply.");
        else
            _logger.LogInformation("Recorded {Count} migration step(s).", applied.Count);

        return applied;
    }

    /// <inheritdoc />
    public async Task<MigrationValidationResult> ValidateAsync(CancellationToken cancellationToken = default) {
        var steps = MigrationStepCatalog.Build(_sources, _services);

        await using var session = await _database.ConnectAsync(false, cancellationToken);
        var rows = await LoadIfExistsAsync(session, cancellationToken);

        var plan = MigrationPlanner.Build(steps, rows);
        var errors = new List<MigrationValidationError>();
        errors.AddRange(steps.Errors);
        errors.AddRange(plan.Errors);
        foreach (var row in plan.FailedRows)
            errors.Add(new MigrationValidationError {
                StepName = row.StepName,
                Message = FailedRowMessage(row)
            });

        if (_options.OutOfOrder == OutOfOrderPolicy.Deny)
            foreach (var planned in plan.OutOfOrder)
                errors.Add(new MigrationValidationError {
                    StepName = planned.Step.Name,
                    Message = OutOfOrderMessage(planned)
                });

        return new MigrationValidationResult {
            Errors = errors,
            Pending = plan.Pending.Select(p => p.ToInfo()).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MigrationStepInfo>> GetPendingAsync(CancellationToken cancellationToken = default) {
        var steps = DiscoverOrThrow();

        await using var session = await _database.ConnectAsync(false, cancellationToken);
        var rows = await LoadIfExistsAsync(session, cancellationToken);

        return MigrationPlanner.Build(steps, rows).Pending.Select(p => p.ToInfo()).ToList();
    }

    /// <inheritdoc />
    public async Task BaselineAsync(string version, string? description = null,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed))
            throw new MigrationException(
                $"Baseline version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");

        await using var session = await _database.ConnectAsync(true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        var rows = await session.LoadHistoryAsync(cancellationToken);
        if (rows.Count > 0)
            throw new MigrationException(
                $"Cannot baseline at version {parsed.Text}: the history table already has {rows.Count} row(s). "
                + "Baselining is only for adopting an existing database before its first migration run.");

        await session.InsertHistoryRowAsync(
            new MigrationHistoryRecord {
                InstalledRank = 1,
                Kind = MigrationOutcomes.Baseline,
                Version = parsed.Text,
                Description = description ?? "baseline",
                StepName = $"baseline {parsed.Text}",
                Checksum = null,
                Outcome = MigrationOutcomes.Baseline,
                DurationMs = 0
            },
            null,
            cancellationToken);
        _logger.LogInformation("Baselined schema history at version {Version}.", parsed.Text);
    }

    /// <inheritdoc />
    public async Task ResolveFailedAsync(string version, ResolveAction action,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!MigrationVersion.TryParse(version, out var parsed))
            throw new MigrationException(
                $"Version '{version}' is malformed; expected numeric segments separated by '.' or '_'.");

        var steps = MigrationStepCatalog.Build(_sources, _services);

        await using var session = await _database.ConnectAsync(true, cancellationToken);
        await session.EnsureHistoryTableAsync(cancellationToken);
        var rows = await session.LoadHistoryAsync(cancellationToken);
        var failed = rows.FirstOrDefault(row =>
            row.Outcome == MigrationOutcomes.Failed
            && row.Version is not null
            && MigrationVersion.TryParse(row.Version, out var rowVersion)
            && rowVersion.Equals(parsed));
        if (failed is null)
            throw new MigrationException($"No failed migration with version {parsed.Text} exists in the history.");

        if (action == ResolveAction.Retry) {
            await session.DeleteHistoryRowAsync(failed.InstalledRank, cancellationToken);
            _logger.LogInformation(
                "Resolved failed migration {StepName} (version {Version}) as Retry; the next migrate reruns it.",
                failed.StepName, parsed.Text);
        }
        else {
            var planned = steps.Versioned.FirstOrDefault(p => p.Version!.Equals(parsed));
            await session.MarkHistoryRowAppliedAsync(failed.InstalledRank, planned?.Step.Checksum, cancellationToken);
            _logger.LogInformation(
                "Resolved failed migration {StepName} (version {Version}) as MarkApplied.",
                failed.StepName, parsed.Text);
        }
    }

    private static async Task<IReadOnlyList<AppliedMigrationRow>> LoadIfExistsAsync(IMigrationSession session,
        CancellationToken cancellationToken) {
        return await session.HistoryTableExistsAsync(cancellationToken)
            ? await session.LoadHistoryAsync(cancellationToken)
            : [];
    }

    private MigrationStepCatalog DiscoverOrThrow() {
        var steps = MigrationStepCatalog.Build(_sources, _services);
        if (steps.Errors.Count > 0)
            throw new MigrationException(
                "Migration step validation failed:\n" +
                string.Join("\n", steps.Errors.Select(e => "- " + e.Message)));

        return steps;
    }

    private void ThrowIfBlocked(MigrationPlan plan) {
        if (plan.FailedRows.Count > 0) {
            var row = plan.FailedRows[0];
            throw new MigrationFailedStateException(row.Version ?? "", row.StepName, FailedRowMessage(row));
        }

        if (plan.Errors.Count > 0)
            throw new MigrationException(
                "Migration validation failed:\n" + string.Join("\n", plan.Errors.Select(e => "- " + e.Message)));

        if (_options.OutOfOrder == OutOfOrderPolicy.Deny && plan.OutOfOrder.Count > 0)
            throw new MigrationException(
                "Out-of-order migrations denied (OutOfOrderPolicy.Deny):\n"
                + string.Join("\n", plan.OutOfOrder.Select(p => "- " + OutOfOrderMessage(p))));
    }

    private async Task<MigrationStepResult> ApplyAsync(IMigrationSession session, PlannedStep planned, int installedRank,
        CancellationToken cancellationToken) {
        var step = planned.Step;
        _logger.LogInformation("Applying migration {StepName} ({Kind})…", step.Name, step.Kind);
        var stopwatch = Stopwatch.StartNew();

        // A fresh scope per step: scoped services (a DbContext, an ISqlSession) never leak between steps.
        await using var scope = AsyncScope();
        MigrationOutcome outcome;
        try {
            outcome = await RunStepAsync(session, planned, scope.ServiceProvider, installedRank, stopwatch,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not MigrationException and not OperationCanceledException) {
            throw new MigrationExecutionException(step.Name, FailureMessage(step, ex), ex);
        }

        _logger.LogInformation("Migration {StepName} {Outcome} in {DurationMs} ms.", step.Name,
            MigrationOutcomes.ToWire(outcome), stopwatch.ElapsedMilliseconds);
        return new MigrationStepResult { Step = planned.ToInfo(), Outcome = outcome, Duration = stopwatch.Elapsed };
    }

    private async Task<MigrationOutcome> RunStepAsync(IMigrationSession session, PlannedStep planned,
        IServiceProvider services, int installedRank, Stopwatch stopwatch, CancellationToken cancellationToken) {
        var step = planned.Step;

        // Repeatables have no "already done" notion beyond their checksum; versioned steps may declare one.
        if (planned.Version is not null
            && await step.IsAlreadySatisfiedAsync(new MigrationStepContext(step, services, session, null),
                cancellationToken)) {
            await session.InsertHistoryRowAsync(
                Row(planned, installedRank, MigrationOutcomes.Satisfied, stopwatch.ElapsedMilliseconds), null,
                cancellationToken);
            return MigrationOutcome.Satisfied;
        }

        if (step.UseTransaction) {
            // The history row commits atomically with the step — the no-repair invariant. A throw leaves the
            // transaction uncommitted; disposing it rolls the step and its row back together.
            await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
            await step.ExecuteAsync(new MigrationStepContext(step, services, session, transaction), cancellationToken);
            await session.InsertHistoryRowAsync(
                Row(planned, installedRank, MigrationOutcomes.Applied, stopwatch.ElapsedMilliseconds), transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MigrationOutcome.Applied;
        }

        try {
            await step.ExecuteAsync(new MigrationStepContext(step, services, session, null), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            if (planned.Version is not null && step.RecordsFailedRow)
                await RecordFailureAsync(session, planned, installedRank, stopwatch, cancellationToken);

            throw;
        }

        await session.InsertHistoryRowAsync(
            Row(planned, installedRank, MigrationOutcomes.Applied, stopwatch.ElapsedMilliseconds), null,
            cancellationToken);
        return MigrationOutcome.Applied;
    }

    private AsyncServiceScopeHolder AsyncScope() {
        return new AsyncServiceScopeHolder(_services);
    }

    private async Task RecordFailureAsync(IMigrationSession session, PlannedStep planned, int installedRank,
        Stopwatch stopwatch, CancellationToken cancellationToken) {
        try {
            await session.InsertHistoryRowAsync(
                Row(planned, installedRank, MigrationOutcomes.Failed, stopwatch.ElapsedMilliseconds), null,
                cancellationToken);
        }
        catch (Exception recordEx) {
            // The connection may be gone entirely; the original failure is the one worth surfacing.
            _logger.LogError(recordEx, "Failed to record the failed history row for migration {StepName}.",
                planned.Step.Name);
        }
    }

    private static MigrationHistoryRecord Row(PlannedStep planned, int installedRank, string outcome, long durationMs) {
        return new MigrationHistoryRecord {
            InstalledRank = installedRank,
            Kind = planned.Step.Kind,
            Version = planned.Version?.Text,
            Description = planned.Step.Description,
            StepName = planned.Step.Name,
            Checksum = planned.Step.Checksum,
            Outcome = outcome,
            DurationMs = durationMs
        };
    }

    private static string FailureMessage(MigrationStep step, Exception ex) {
        if (step.UseTransaction)
            return $"Migration '{step.Name}' failed and was rolled back; no history row was recorded. "
                   + $"Fix the step and rerun. Cause: {ex.Message}";

        if (step.Version is not null && step.RecordsFailedRow)
            return
                $"Migration '{step.Name}' failed while running outside a transaction and may be half-applied; "
                + "a failed history row was recorded and subsequent runs fail closed. Resolve it with "
                + $"IMigrationRunner.ResolveFailedAsync(\"{step.Version}\", ResolveAction.Retry | MarkApplied). Cause: {ex.Message}";

        return $"Migration '{step.Name}' failed while running outside a transaction; nothing was recorded and the "
               + "next run retries it. Its partial writes remain, so it must be idempotent. "
               + $"Cause: {ex.Message}";
    }

    private static string FailedRowMessage(AppliedMigrationRow row) {
        return
            $"Migration '{row.StepName}' (version {row.Version}) previously failed while running outside a transaction "
            + $"and may be half-applied. Resolve it with IMigrationRunner.ResolveFailedAsync(\"{row.Version}\", "
            + "ResolveAction.Retry | MarkApplied) before migrating again.";
    }

    private static string OutOfOrderMessage(PlannedStep planned) {
        return
            $"Migration '{planned.Step.Name}' (version {planned.Version!.Text}) is versioned below an already-applied migration.";
    }

    /// <summary>
    /// Disposes the per-step service scope. A runner built without a service provider has no scope factory;
    /// the holder then hands out the empty provider and disposes nothing.
    /// </summary>
    private sealed class AsyncServiceScopeHolder : IAsyncDisposable {
        private readonly AsyncServiceScope? _scope;

        public AsyncServiceScopeHolder(IServiceProvider services) {
            if (services is EmptyServiceProvider) {
                ServiceProvider = services;
                return;
            }

            _scope = services.CreateAsyncScope();
            ServiceProvider = _scope.Value.ServiceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public ValueTask DisposeAsync() {
            return _scope?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }
}
