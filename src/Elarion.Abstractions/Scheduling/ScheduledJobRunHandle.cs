namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Identifies a queued or scheduled in-memory job occurrence.
/// </summary>
/// <remarks>
/// Runtime scheduling returns this handle immediately after accepting the job. The handle is
/// not durable; queued jobs and status are lost if the process restarts.
/// </remarks>
public sealed record ScheduledJobRunHandle {
    /// <summary>
    /// Stable logical job identifier across deferred retry attempts.
    /// </summary>
    /// <remarks>
    /// Return this id from application handlers when another handler or UI needs to observe
    /// status with <see cref="IJobSchedulerInspector.GetJob(Guid)"/>.
    /// </remarks>
    public required Guid JobId { get; init; }

    /// <summary>
    /// Unique identifier for the accepted attempt represented by this handle.
    /// </summary>
    /// <remarks>
    /// In deferred retry mode, later attempts get new run ids while retaining the same
    /// <see cref="JobId"/>. Most application code should store <see cref="JobId"/>; this value
    /// is primarily for diagnostics, snapshots, correlating individual attempts, and
    /// <see cref="IJobScheduler.CancelRunAsync(Guid, CancellationToken)"/>.
    /// </remarks>
    public required Guid RunId { get; init; }

    /// <summary>The stable job name.</summary>
    public required string JobName { get; init; }

    /// <summary>The instant the run is due to execute.</summary>
    public required DateTimeOffset DueTimeUtc { get; init; }

    /// <summary>
    /// First attempt number represented by this handle.
    /// </summary>
    /// <remarks>
    /// Newly scheduled jobs start at attempt <c>1</c>. Deferred retry updates attempt numbers
    /// in <see cref="ScheduledJobState"/> as future attempts are enqueued.
    /// </remarks>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// Maximum attempts including the original attempt.
    /// </summary>
    /// <remarks>
    /// For a policy with <c>MaxRetryAttempts = 2</c>, this value is <c>3</c>: the original
    /// attempt plus two retries.
    /// </remarks>
    public int MaxAttempts { get; init; } = 1;
}
