namespace Elarion.Migrations;

/// <summary>How <see cref="IMigrationRunner.ResolveFailedAsync"/> resolves a failed no-transaction migration.</summary>
public enum ResolveAction {
    /// <summary>
    /// Delete the failed history row so the next <see cref="IMigrationRunner.MigrateAsync"/> reruns the
    /// script. The script must be idempotent against the partially applied state (or the partial state
    /// must have been reverted by hand).
    /// </summary>
    Retry,

    /// <summary>
    /// Declare the version applied — the schema change was completed by hand — turning the failed row
    /// into an applied one, re-checksummed against the current script content.
    /// </summary>
    MarkApplied,
}
