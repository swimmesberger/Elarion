namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Public in-memory state for one runtime-scheduled logical job.
/// </summary>
/// <remarks>
/// This state is intended for lightweight status polling after a handler enqueues background
/// work and returns a <see cref="ScheduledJobRunHandle.JobId"/>. It is kept only in memory and
/// terminal states are pruned according to <see cref="SchedulerOptions.MaxRetainedCompletedJobs"/>.
/// </remarks>
public sealed record ScheduledJobState {
    /// <summary>The stable logical job identifier across retry attempts.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The current or most recent attempt run identifier.</summary>
    public required Guid CurrentRunId { get; init; }

    /// <summary>The stable job name.</summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Current lifecycle status of the logical job, spanning all deferred retry attempts.
    /// </summary>
    public required ScheduledJobLifecycleStatus Status { get; init; }

    /// <summary>
    /// Current 1-based attempt number.
    /// </summary>
    /// <remarks>
    /// The value increases when scheduler-deferred retry enqueues a future attempt. Inline
    /// resilience retries are not represented as separate scheduler attempts.
    /// </remarks>
    public required int Attempt { get; init; }

    /// <summary>The maximum attempts including the original attempt.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Next retry due time when <see cref="Status"/> is
    /// <see cref="ScheduledJobLifecycleStatus.WaitingRetry"/>.
    /// </summary>
    /// <remarks>
    /// Null means the job is not currently waiting for a deferred retry attempt.
    /// </remarks>
    public DateTimeOffset? NextAttemptDueTimeUtc { get; init; }

    /// <summary>Optional caller-provided correlation value.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Last failure or policy message observed for the logical job, if any.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>When the logical job was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the logical job reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }
}
