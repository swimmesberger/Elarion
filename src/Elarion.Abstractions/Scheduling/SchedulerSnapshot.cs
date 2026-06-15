namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Point-in-time state captured from the in-memory scheduler.
/// </summary>
/// <remarks>
/// Snapshot data is best-effort operational state for health checks or admin UI. It is not a
/// durable audit log and can change immediately after the snapshot is captured.
/// </remarks>
public sealed record SchedulerSnapshot {
    /// <summary>The instant the snapshot was captured.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// Registered job descriptors known to the scheduler, including runtime-only job types.
    /// </summary>
    public required IReadOnlyList<ScheduledJobDescriptorInfo> Jobs { get; init; }

    /// <summary>Queued runs that have not started yet.</summary>
    public required IReadOnlyList<ScheduledJobRunInfo> QueuedRuns { get; init; }

    /// <summary>Runs currently executing.</summary>
    public required IReadOnlyList<ScheduledJobRunInfo> ActiveRuns { get; init; }

    /// <summary>
    /// Most recent terminal outcome per job name.
    /// </summary>
    /// <remarks>
    /// Only the latest outcome per job name is kept in the snapshot. Use
    /// <see cref="IJobSchedulerInspector.GetJob(Guid)"/> for current runtime-job state by
    /// logical job id.
    /// </remarks>
    public required IReadOnlyList<ScheduledJobOutcomeInfo> RecentOutcomes { get; init; }
}

/// <summary>
/// Public scheduler metadata for one registered job.
/// </summary>
public sealed record ScheduledJobDescriptorInfo {
    /// <summary>The stable job name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Static schedule kind for recurring/startup jobs, or null for runtime-only job types.
    /// </summary>
    public ScheduledJobScheduleKind? ScheduleKind { get; init; }

    /// <summary>True when this job can be scheduled through <see cref="IJobScheduler"/>.</summary>
    public required bool SupportsRuntimeScheduling { get; init; }

    /// <summary>Optional group key used for cross-job serialization.</summary>
    public string? Group { get; init; }

    /// <summary>The overlap policy for recurring occurrences of this job.</summary>
    public required ScheduledJobOverlap Overlap { get; init; }

    /// <summary>The misfire policy for recurring fixed-rate and cron occurrences.</summary>
    public required ScheduledJobMisfirePolicy MisfirePolicy { get; init; }

    /// <summary>Maximum concurrent executions for this job; 0 means no job-local cap.</summary>
    public required int MaxConcurrentRuns { get; init; }

    /// <summary>
    /// Next queued due time for this job, if one is currently known.
    /// </summary>
    /// <remarks>
    /// This is the value a dashboard can display as "next fire time". It is null for disabled
    /// schedules, runtime-only jobs with no queued run, or jobs whose next occurrence has not
    /// been enqueued yet.
    /// </remarks>
    public DateTimeOffset? NextDueTimeUtc { get; init; }
}

/// <summary>
/// Public state for a queued or active run.
/// </summary>
public sealed record ScheduledJobRunInfo {
    /// <summary>The unique run identifier.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The stable job name.</summary>
    public required string JobName { get; init; }

    /// <summary>The instant this run is or was due.</summary>
    public required DateTimeOffset DueTimeUtc { get; init; }

    /// <summary>The instant this run started, if it has started.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>True when this occurrence was created through <see cref="IJobScheduler"/>.</summary>
    public required bool IsRuntimeScheduled { get; init; }

    /// <summary>The current state of the run.</summary>
    public required ScheduledJobRunStatus Status { get; init; }
}

/// <summary>
/// Public terminal outcome for a completed, failed, cancelled, or skipped run.
/// </summary>
public sealed record ScheduledJobOutcomeInfo {
    /// <summary>The unique run identifier.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The stable job name.</summary>
    public required string JobName { get; init; }

    /// <summary>The instant this run was due.</summary>
    public required DateTimeOffset DueTimeUtc { get; init; }

    /// <summary>The instant this run started, if it reached execution.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>The instant this run reached a terminal outcome.</summary>
    public required DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>True when this occurrence was created through <see cref="IJobScheduler"/>.</summary>
    public required bool IsRuntimeScheduled { get; init; }

    /// <summary>The terminal state of the run.</summary>
    public required ScheduledJobRunStatus Status { get; init; }

    /// <summary>
    /// Optional scheduler policy reason or exception message.
    /// </summary>
    /// <remarks>
    /// Examples include <c>overlap</c>, <c>misfire</c>, <c>cancelled</c>, or the exception
    /// message from a failed job.
    /// </remarks>
    public string? Message { get; init; }
}
