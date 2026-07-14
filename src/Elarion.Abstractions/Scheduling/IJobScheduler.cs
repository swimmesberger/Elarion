namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Schedules runtime-created in-memory jobs that are known to the source-generated scheduler registry.
/// </summary>
/// <remarks>
/// Runtime jobs must be annotated with <see cref="ScheduledJobAttribute"/> and implement
/// <see cref="IScheduledJob{TPayload}"/> so the source generator can emit a typed invocation
/// descriptor. This API is in-memory only; accepted jobs do not survive process restart.
/// A disabled scheduler (<see cref="SchedulerOptions.Enabled"/> is <see langword="false"/>) rejects
/// every enqueue/schedule call with <see cref="InvalidOperationException"/>: its dispatch loop never
/// runs, so accepting work would only grow the queue without bound.
/// </remarks>
public interface IJobScheduler {
    /// <summary>
    /// Queues a one-off runtime job that should become eligible immediately.
    /// </summary>
    /// <remarks>
    /// Use this method for "run this in the background now" work. The returned
    /// <see cref="ScheduledJobRunHandle"/> means the scheduler accepted the run; it does not
    /// mean the job has started or completed. Actual execution still depends on scheduler
    /// capacity, overlap rules, cancellation, shutdown, and resilience options.
    /// </remarks>
    ValueTask<ScheduledJobRunHandle> EnqueueAsync<TJob, TPayload>(
        TPayload payload,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload>;

    /// <summary>
    /// Queues a one-off runtime job that should become eligible immediately, with per-job
    /// runtime options.
    /// </summary>
    /// <remarks>
    /// This overload is the immediate counterpart to
    /// <see cref="ScheduleAsync{TJob, TPayload}(TPayload, DateTimeOffset, ScheduledJobOptions?, CancellationToken)"/>.
    /// Use it when there is no future due time and the only scheduling constraints are the
    /// supplied <paramref name="options"/> and the scheduler's current capacity.
    /// </remarks>
    ValueTask<ScheduledJobRunHandle> EnqueueAsync<TJob, TPayload>(
        TPayload payload,
        ScheduledJobOptions? options,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload>;

    /// <summary>
    /// Schedules a one-off runtime job that should not become eligible before the given UTC
    /// instant.
    /// </summary>
    /// <remarks>
    /// Use this method for "run this later" work. Unlike
    /// <see cref="EnqueueAsync{TJob, TPayload}(TPayload, CancellationToken)"/>, the job is held
    /// in the in-memory queue until <paramref name="dueTimeUtc"/> is reached, then competes for
    /// scheduler capacity like any other eligible run. If <paramref name="dueTimeUtc"/> is in
    /// the past, the job is eligible immediately, which makes it effectively equivalent to
    /// <see cref="EnqueueAsync{TJob, TPayload}(TPayload, CancellationToken)"/>. Recurring
    /// misfire policy does not apply to one-off runtime jobs.
    /// </remarks>
    ValueTask<ScheduledJobRunHandle> ScheduleAsync<TJob, TPayload>(
        TPayload payload,
        DateTimeOffset dueTimeUtc,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload>;

    /// <summary>
    /// Schedules a one-off runtime job that should not become eligible before the given UTC
    /// instant, with per-job runtime options.
    /// </summary>
    /// <remarks>
    /// This overload is the delayed counterpart to
    /// <see cref="EnqueueAsync{TJob, TPayload}(TPayload, ScheduledJobOptions?, CancellationToken)"/>.
    /// Use it when the caller needs both a future due time and runtime options such as overlap
    /// or resilience behavior.
    ///
    /// Use <see cref="ScheduledJobOptions.ResilienceMode"/> with
    /// <see cref="ScheduledJobResilienceMode.DeferredRetry"/> when the caller needs a stable
    /// <see cref="ScheduledJobRunHandle.JobId"/> to observe between retry attempts.
    /// </remarks>
    ValueTask<ScheduledJobRunHandle> ScheduleAsync<TJob, TPayload>(
        TPayload payload,
        DateTimeOffset dueTimeUtc,
        ScheduledJobOptions? options,
        CancellationToken ct = default)
        where TJob : IScheduledJob<TPayload>;

    /// <summary>
    /// Cancels one concrete run attempt.
    /// </summary>
    /// <remarks>
    /// Pass <see cref="ScheduledJobRunHandle.RunId"/> when you intentionally want to target the
    /// currently accepted attempt rather than the whole logical job. This is useful for
    /// diagnostic or operator workflows that act on an entry from
    /// <see cref="IJobSchedulerInspector.GetSnapshot()"/>.
    ///
    /// If the run is still queued or dispatching, it is prevented from starting. If it is
    /// already active, the scheduler requests cooperative cancellation through the run's
    /// execution token. Deferred retry may create later attempts with different run ids, so
    /// use <see cref="CancelJobAsync(Guid, CancellationToken)"/> for user-facing "cancel this
    /// background operation" flows.
    /// </remarks>
    /// <returns><c>true</c> when the run attempt was found and cancellation was requested; otherwise <c>false</c>.</returns>
    ValueTask<bool> CancelRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a logical runtime-scheduled job independent of its current state.
    /// </summary>
    /// <remarks>
    /// Pass <see cref="ScheduledJobRunHandle.JobId"/> when the caller thinks in terms of a
    /// background operation rather than an individual attempt. This is the preferred API for
    /// application handlers, UI actions, and user-facing cancellation.
    ///
    /// The scheduler maps the logical job id to the current concrete state: a queued run is
    /// prevented from starting, a waiting deferred-retry attempt is cancelled, and an active
    /// run receives cooperative cancellation through its execution token. The same job id stays
    /// stable across deferred retry attempts.
    /// </remarks>
    /// <returns><c>true</c> when the logical job exists and cancellation was requested; otherwise <c>false</c>.</returns>
    ValueTask<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default);
}
