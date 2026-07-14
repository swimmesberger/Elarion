namespace Elarion.Migrations;

/// <summary>
/// Applies embedded SQL migration scripts to a database (ADR-0057/ADR-0060). Scripts are embedded
/// resources named <c>V{version}__{description}.sql</c> (versioned, applied once, in version order) or
/// <c>R__{description}.sql</c> (repeatable, re-applied whenever its checksum changes). The runner is the
/// EF-free (NativeAOT) tier's schema tool — EF-based applications keep EF Core migrations. The
/// database-specific work (locking, history-table SQL, script execution) is supplied by a provider
/// through <see cref="IMigrationDatabase"/>; the engine here is provider-neutral.
/// </summary>
/// <remarks>
/// <para>
/// Execution uses one dedicated connection guarded by an exclusive migration lock, so concurrent
/// startups serialize and a crashed runner releases the lock with its connection. Each versioned script
/// runs in its own transaction and its history row commits in that same transaction — a failed
/// transactional migration leaves no history row and is simply rerun after the script is fixed. There is
/// deliberately no repair command and no undo: roll forward.
/// </para>
/// <para>
/// A script whose leading comment block contains <c>-- elarion: no-transaction</c> runs outside a
/// transaction for DDL a database forbids inside one (PostgreSQL's <c>CREATE INDEX CONCURRENTLY</c>, …).
/// Only such a script can fail half-applied; for a versioned script the runner then records an explicit
/// failed history row and every subsequent run fails closed until <see cref="ResolveFailedAsync"/>
/// decides between retrying and marking the version applied. A failed <em>repeatable</em> script records
/// nothing — repeatables are idempotent by doctrine and their changed checksum was never recorded, so
/// the next run simply retries them.
/// </para>
/// </remarks>
public interface IMigrationRunner {
    /// <summary>
    /// Validates checksums, then applies all pending scripts: versioned scripts in version order
    /// (out-of-order arrivals per <see cref="MigrationOptions.OutOfOrder"/>), repeatable
    /// scripts afterwards in name order when their checksum changed.
    /// </summary>
    /// <returns>The scripts applied by this run, in execution order; empty when the schema was up to date.</returns>
    /// <exception cref="MigrationException">
    /// A script resource is invalid, an applied script's checksum changed, or out-of-order scripts were
    /// found under <see cref="OutOfOrderPolicy.Deny"/>.
    /// </exception>
    /// <exception cref="MigrationFailedStateException">A previous no-transaction migration failed and has not been resolved.</exception>
    /// <exception cref="MigrationExecutionException">A script failed while executing.</exception>
    Task<IReadOnlyList<MigrationScriptInfo>> MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports, without writing anything: script-resource problems, checksum mismatches against applied
    /// history, unresolved failed migrations, and the scripts a <see cref="MigrateAsync"/> would apply.
    /// </summary>
    Task<MigrationValidationResult> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the scripts a <see cref="MigrateAsync"/> would apply, in execution order, without writing anything.</summary>
    /// <exception cref="MigrationException">A script resource is invalid.</exception>
    Task<IReadOnlyList<MigrationScriptInfo>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an existing database as already at <paramref name="version"/>: scripts at or below it are
    /// treated as applied and never run. Allowed only while the history is empty — baselining is an
    /// explicit adoption step, never an automatic fallback.
    /// </summary>
    /// <param name="version">The version the existing schema corresponds to, e.g. <c>"20260713093000"</c> or <c>"1.2"</c>.</param>
    /// <param name="description">Optional description recorded on the baseline row; defaults to <c>baseline</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock or the write.</param>
    /// <exception cref="MigrationException">The version is malformed or the history is not empty.</exception>
    Task BaselineAsync(string version, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the failed history row of a no-transaction migration: <see cref="ResolveAction.Retry"/>
    /// deletes the row so the next <see cref="MigrateAsync"/> reruns the (fixed, idempotent) script;
    /// <see cref="ResolveAction.MarkApplied"/> declares the version applied — after the schema was
    /// completed by hand — and re-checksums the row against the current script content.
    /// </summary>
    /// <param name="version">The version of the failed migration, as reported by the fail-closed error.</param>
    /// <param name="action">The resolution to apply.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock or the write.</param>
    /// <exception cref="MigrationException">No failed migration with that version exists.</exception>
    Task ResolveFailedAsync(string version, ResolveAction action, CancellationToken cancellationToken = default);
}
