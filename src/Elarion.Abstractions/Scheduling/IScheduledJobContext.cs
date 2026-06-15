namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Carries metadata and cooperative cancellation control for a single scheduled job execution.
/// </summary>
/// <remarks>
/// A new context is created for every scheduler run attempt. In scheduler-deferred retry,
/// each retry attempt receives a new <see cref="RunId"/> and context. The concrete context
/// implementation is owned by the scheduler runtime, so scheduler-specific state does not leak
/// into the public job-authoring API.
/// </remarks>
public interface IScheduledJobContext {
    /// <summary>The unique run identifier for this occurrence.</summary>
    Guid RunId { get; }

    /// <summary>The stable scheduled job name.</summary>
    string JobName { get; }

    /// <summary>The instant this occurrence was due to run.</summary>
    DateTimeOffset DueTimeUtc { get; }

    /// <summary>The instant this occurrence actually started running.</summary>
    DateTimeOffset StartedAtUtc { get; }

    /// <summary>
    /// Delay between the due time and actual start time.
    /// </summary>
    /// <remarks>
    /// A positive value indicates scheduling lag caused by concurrency limits, overlap/group
    /// serialization, host pauses, or clock changes.
    /// </remarks>
    TimeSpan SchedulingLag { get; }

    /// <summary>True when this occurrence was created dynamically through <see cref="IJobScheduler"/>.</summary>
    bool IsRuntimeScheduled { get; }

    /// <summary>
    /// Requests cooperative cancellation for this run.
    /// </summary>
    /// <remarks>
    /// The scheduler cancels the run token, but job code must pass and observe the supplied
    /// <see cref="CancellationToken"/> for work to stop promptly.
    /// </remarks>
    void RequestCancellation();
}
