namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Exposes lightweight in-memory scheduler state for health checks and admin surfaces.
/// </summary>
/// <remarks>
/// The inspector reports process-local operational state. It is not durable and should not be
/// used as an audit log or source of truth after process restart.
/// </remarks>
public interface IJobSchedulerInspector {
    /// <summary>
    /// Captures a point-in-time scheduler snapshot for dashboards, diagnostics, or health checks.
    /// </summary>
    SchedulerSnapshot GetSnapshot();

    /// <summary>
    /// Gets in-memory state for one runtime-scheduled logical job by stable job id.
    /// </summary>
    /// <returns>
    /// The current state, or null when the id was never known, the process restarted, or a
    /// terminal state aged out of retention.
    /// </returns>
    ScheduledJobState? GetJob(Guid jobId);
}
