namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Describes the lifecycle state of a logical runtime-scheduled job.
/// </summary>
public enum ScheduledJobLifecycleStatus {
    /// <summary>
    /// The logical runtime job has a queued attempt that has not started yet.
    /// </summary>
    Queued,

    /// <summary>
    /// The current attempt is running.
    /// </summary>
    Running,

    /// <summary>
    /// A failed attempt scheduled a future retry and no attempt is currently active.
    /// </summary>
    WaitingRetry,

    /// <summary>The job completed successfully.</summary>
    Succeeded,

    /// <summary>
    /// The job failed terminally because retries were exhausted or the failure was not retryable.
    /// </summary>
    Failed,

    /// <summary>The job was cancelled before or during execution.</summary>
    Cancelled,

    /// <summary>
    /// The job was skipped by scheduler policy, for example because the descriptor was disabled.
    /// </summary>
    Skipped
}
