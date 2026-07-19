namespace Elarion.Migrations;

/// <summary>
/// What <see cref="IMigrationRunner.MigrateAsync"/> does with a pending script whose version is below an
/// already-applied one — the branch-merge case where a lower-versioned script lands after a higher one ran.
/// </summary>
public enum OutOfOrderPolicy {
    /// <summary>
    /// Apply the script, log a warning, and record it in true execution order. The default: the history
    /// table records what actually happened either way, and timestamp versions make the case rare —
    /// a strict default only teaches teams a global escape flag (ADR-0057).
    /// </summary>
    Warn,

    /// <summary>Fail the run, naming the out-of-order scripts. The opt-in for teams that want strict version ordering.</summary>
    Deny
}
