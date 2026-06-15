namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Describes the scheduler-visible state of a job occurrence.
/// </summary>
public enum ScheduledJobRunStatus {
    /// <summary>
    /// The run is queued and has not started.
    /// </summary>
    Queued,

    /// <summary>
    /// The run has started and is currently executing or waiting inside scheduler-owned gates.
    /// </summary>
    Running,

    /// <summary>The run completed successfully.</summary>
    Succeeded,

    /// <summary>
    /// The run failed with an exception and did not schedule a deferred retry for this run id.
    /// </summary>
    Failed,

    /// <summary>The run was cancelled before or during execution.</summary>
    Cancelled,

    /// <summary>
    /// The run was skipped by scheduler policy such as overlap, disabled state, or misfire handling.
    /// </summary>
    Skipped
}
