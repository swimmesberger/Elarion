namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Configures the in-memory scheduler runtime.
/// </summary>
/// <remarks>
/// These options affect only the current process. The scheduler does not persist queued
/// runtime jobs, retry state, or recurring due times across restarts.
/// </remarks>
public sealed record SchedulerOptions {
    /// <summary>
    /// Whether the hosted scheduler loop should enqueue recurring jobs and process queued work.
    /// </summary>
    /// <remarks>
    /// When disabled, the scheduler service starts but does not execute jobs. Runtime schedule
    /// calls can still be accepted by the in-memory API, but they will not run until a scheduler
    /// instance is enabled.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum number of job occurrences that may execute at the same time across all jobs.
    /// </summary>
    /// <remarks>
    /// Values below one are normalized by configuration-based registration. This is a global
    /// capacity limit; job-level overlap and <see cref="ScheduledJobAttribute.MaxConcurrentRuns"/>
    /// may apply stricter limits.
    /// </remarks>
    public int MaxConcurrentExecutions { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Maximum number of terminal runtime job states retained for
    /// <see cref="IJobSchedulerInspector.GetJob(Guid)"/> lookups.
    /// </summary>
    /// <remarks>
    /// This bounds memory used by completed runtime jobs. When the limit is exceeded, older
    /// terminal states are forgotten; active and waiting-retry jobs are not pruned by this cap.
    /// </remarks>
    public int MaxRetainedCompletedJobs { get; init; } = 1024;

    /// <summary>
    /// Maximum additional missed occurrences a <see cref="ScheduledJobMisfirePolicy.CatchUp"/>
    /// chain may enqueue before coalescing to the next future occurrence.
    /// </summary>
    /// <remarks>
    /// The cap prevents unbounded bursts after long pauses. Set to <c>0</c> to make
    /// <see cref="ScheduledJobMisfirePolicy.CatchUp"/> run the first overdue occurrence and
    /// then coalesce immediately.
    /// </remarks>
    public int MaxMisfireCatchUpRuns { get; init; } = 32;
}
