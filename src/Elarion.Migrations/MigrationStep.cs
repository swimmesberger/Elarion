namespace Elarion.Migrations;

/// <summary>The built-in <see cref="MigrationStep.Kind"/> values; a step source may introduce its own.</summary>
public static class MigrationStepKinds {
    /// <summary>An embedded <c>V…__</c>/<c>R__</c> SQL script (<c>sql</c>).</summary>
    public const string Sql = "sql";

    /// <summary>A C# <see cref="ICodeMigration"/> (<c>code</c>).</summary>
    public const string Code = "code";
}

/// <summary>
/// One unit of work in the migration plan (ADR-0081): a SQL script, a C# code migration, an EF Core
/// migration — anything a <see cref="IMigrationStepSource"/> contributes. Every step carries a version, and
/// the runner orders <em>all</em> steps of <em>all</em> sources in one sequence by it, runs them under one
/// lock, and records them in one history, so an expand → backfill → contract release interleaves SQL, code
/// and EF steps in the order their versions say.
/// </summary>
/// <remarks>
/// A versioned step runs exactly once; a step with a <see langword="null"/> <see cref="Version"/> is
/// repeatable and is re-applied whenever its <see cref="Checksum"/> changes (only SQL <c>R__</c> scripts use
/// this). Subclass it to add a step source; application code normally implements <see cref="ICodeMigration"/>
/// instead.
/// </remarks>
public abstract class MigrationStep {
    /// <summary>The step kind recorded in the history (see <see cref="MigrationStepKinds"/>).</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// The step's stable name, recorded in the history: the script file name, the migration type's name, the
    /// EF migration id. Repeatable steps are identified by it, so it must be unique among them.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// The version that orders the step: numeric segments separated by <c>.</c> or <c>_</c>
    /// (<c>20260713093000</c>, <c>1.2</c>); <see langword="null"/> for a repeatable step. Versions are unique
    /// across <em>all</em> steps of the plan, whatever their kind.
    /// </summary>
    public abstract string? Version { get; }

    /// <summary>A short human-readable description, recorded in the history.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Where the step came from (resource name, type name, …) — used only in diagnostics such as a duplicate
    /// version report. Defaults to <see cref="Name"/>.
    /// </summary>
    public virtual string Origin => Name;

    /// <summary>
    /// A content checksum guarding against edits to an applied step (a SQL script's SHA-256), or
    /// <see langword="null"/> when the step has no content to guard (code and EF steps).
    /// </summary>
    public virtual string? Checksum => null;

    /// <summary>
    /// Whether <see cref="ExecuteAsync"/> runs inside a transaction on the plan's connection whose commit also
    /// records the step — so a failure rolls the step's writes and its history row back together and the next
    /// start simply retries. Defaults to <see langword="true"/>. A step that returns <see langword="false"/>
    /// (a long batch, <c>CREATE INDEX CONCURRENTLY</c>) commits as it goes and may fail half-applied; see
    /// <see cref="RecordsFailedRow"/>.
    /// </summary>
    public virtual bool UseTransaction => true;

    /// <summary>
    /// For a non-transactional versioned step that failed: <see langword="true"/> records a <c>failed</c>
    /// history row so every later run fails closed until <see cref="IMigrationRunner.ResolveFailedAsync"/>
    /// decides (SQL <c>no-transaction</c> scripts: the half-applied schema needs a human), and
    /// <see langword="false"/> (the default) records nothing so the next start retries the step (code steps:
    /// they are written idempotently and resumably). Ignored for transactional steps, which leave no trace.
    /// </summary>
    public virtual bool RecordsFailedRow => false;

    /// <summary>
    /// Decides, before <see cref="ExecuteAsync"/> and outside any transaction, that the step has nothing to do
    /// — a fresh install where the legacy source never existed, an adopted database that already has the
    /// schema. Returning <see langword="true"/> records the step as <see cref="MigrationOutcome.Satisfied"/>
    /// without running it. Defaults to <see langword="false"/>.
    /// </summary>
    /// <param name="context">The step context (<see cref="MigrationStepContext.Transaction"/> is <see langword="null"/>).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public virtual ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context,
        CancellationToken cancellationToken) {
        return ValueTask.FromResult(false);
    }

    /// <summary>Performs the step. Throw to fail it: nothing is recorded for it (see <see cref="RecordsFailedRow"/>) and the plan stops.</summary>
    /// <param name="context">The step context.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    public abstract Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken);
}
