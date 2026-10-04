namespace Elarion.Migrations;

/// <summary>
/// A C# step in the migration plan (ADR-0081): backfilling a column with computed data, moving a legacy value
/// into a new table, re-shaping rows — work a SQL script cannot express. It is ordered by
/// <see cref="Version"/> in the <em>same</em> sequence as SQL scripts and EF migrations, so an expand script,
/// this backfill and the following contract script run in that order within one release, once per database,
/// under the one migration lock.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are stateless metadata registered as singletons — compile-time and AOT-safe through a
/// <c>[GenerateContractSetRegistration(typeof(ICodeMigration))]</c> method (ADR-0070), or
/// <see cref="MigrationServiceCollectionExtensions.AddCodeMigration{T}"/> — and must not inject scoped
/// services. Everything the step needs (a <c>DbContext</c>, an <c>ISqlSession</c>, a handler) is resolved from
/// <see cref="MigrationStepContext.Services"/>, a fresh scope per step.
/// </para>
/// <para>
/// A step is recorded only after it completes. By default it runs in the plan's transaction on the plan's
/// connection, so a failure rolls back whatever it wrote through <see cref="MigrationStepContext.Connection"/>,
/// records nothing, stops the plan, fails startup, and is retried on the next start. Writes made through
/// services from <see cref="MigrationStepContext.Services"/> use their own connections and are <em>not</em>
/// rolled back — keep such conversions idempotent (a <c>WHERE</c> that matches only unconverted rows).
/// </para>
/// </remarks>
public interface ICodeMigration {
    /// <summary>
    /// The version that orders this step against every SQL script and EF migration of the plan: numeric
    /// segments, conventionally a timestamp (<c>20260901120000</c>). It is the step's identity in the history —
    /// never change it after the step ran anywhere — and must be unique across all steps.
    /// </summary>
    string Version { get; }

    /// <summary>A short human-readable description, recorded in the history.</summary>
    string Description { get; }

    /// <summary>
    /// Whether <see cref="ExecuteAsync"/> runs inside the plan's transaction, whose commit also records the
    /// step. Defaults to <see langword="true"/>. Return <see langword="false"/> for a long batch conversion
    /// that commits as it goes: it then may fail half-applied, a failure still records nothing, and the next
    /// start reruns it — so it must be idempotent and resumable.
    /// </summary>
    bool UseTransaction => true;

    /// <summary>
    /// Decides, before <see cref="ExecuteAsync"/>, that the conversion has nothing to do — the typical fresh
    /// install, where the legacy source never existed. Returning <see langword="true"/> records the step as
    /// <see cref="MigrationOutcome.Satisfied"/> without running it. Defaults to <see langword="false"/>.
    /// </summary>
    /// <param name="context">The step context (no transaction yet).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        return ValueTask.FromResult(false);
    }

    /// <summary>Performs the conversion. Throw to fail the step: nothing is recorded and startup fails.</summary>
    /// <param name="context">The step context.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken);
}
