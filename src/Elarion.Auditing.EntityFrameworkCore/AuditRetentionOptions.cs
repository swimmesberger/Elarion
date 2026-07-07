namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>Configuration for the opt-in audit-log retention purge.</summary>
/// <remarks>
/// Retention is <b>off by default</b> — an audit trail that silently deletes itself is a compliance bug, so
/// records are kept forever until the host sets <see cref="RetainFor"/> deliberately (ADR-0045).
/// </remarks>
public sealed class AuditRetentionOptions {
    /// <summary>
    /// How long audit records are kept. <see langword="null"/> (the default) keeps them forever and runs no
    /// purge worker.
    /// </summary>
    public TimeSpan? RetainFor { get; set; }

    /// <summary>How often the purge worker runs, when <see cref="RetainFor"/> is set. Default 6 hours.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromHours(6);
}
