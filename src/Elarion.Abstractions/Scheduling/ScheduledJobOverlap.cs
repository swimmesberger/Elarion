namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Defines how a job behaves when another occurrence is already running.
/// </summary>
/// <remarks>
/// Overlap policy is evaluated when an occurrence is about to start and another occurrence
/// with the same job name is already active. It is different from
/// <see cref="ScheduledJobMisfirePolicy"/>, which controls what happens before an overdue
/// fixed-rate or cron occurrence starts.
/// </remarks>
public enum ScheduledJobOverlap {
    /// <summary>
    /// Skips the new occurrence when the same job is already running.
    /// </summary>
    /// <remarks>
    /// This is the safest default for recurring jobs because a slow execution cannot create
    /// a backlog. The skipped occurrence is recorded with reason <c>overlap</c>.
    /// </remarks>
    Skip,

    /// <summary>
    /// Queues one occurrence behind the currently running occurrence and executes it after the
    /// active run releases the job's serialization gate.
    /// </summary>
    /// <remarks>
    /// Recurring queued occurrences are coalesced so a job that is consistently slower than
    /// its interval does not accumulate an unbounded in-memory backlog. Use this when the job
    /// must run sequentially and one pending follow-up is enough.
    /// </remarks>
    Queue,

    /// <summary>
    /// Allows multiple occurrences of the same job to run concurrently.
    /// </summary>
    /// <remarks>
    /// This is appropriate only for reentrant/idempotent jobs. Use
    /// <see cref="ScheduledJobAttribute.MaxConcurrentRuns"/> to add a job-local cap; the
    /// scheduler-wide <see cref="SchedulerOptions.MaxConcurrentExecutions"/> still applies.
    /// </remarks>
    AllowConcurrent
}
