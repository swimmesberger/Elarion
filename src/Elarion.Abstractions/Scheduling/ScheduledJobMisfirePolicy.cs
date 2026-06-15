namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Defines how a recurring fixed-rate or cron job behaves after missed in-process occurrences.
/// </summary>
/// <remarks>
/// A misfire happens when the scheduler observes a fixed-rate or cron occurrence so late that
/// one or more later occurrences would also already be due. This can happen after a long host
/// pause, debugger break, clock jump, or saturated scheduler. Misfire policy does not apply to
/// <see cref="ScheduledJobScheduleKind.FixedDelay"/> jobs or runtime one-off jobs.
/// </remarks>
public enum ScheduledJobMisfirePolicy {
    /// <summary>
    /// Runs the overdue occurrence once, then schedules the next future occurrence and drops
    /// intermediate missed slots.
    /// </summary>
    /// <remarks>
    /// This is the default because it preserves useful work after a pause without creating a
    /// burst. For example, if a one-minute fixed-rate job is delayed by ten minutes, the
    /// scheduler runs one occurrence immediately and then resumes on the next future minute.
    /// </remarks>
    FireOnce,

    /// <summary>
    /// Skips the stale overdue occurrence and schedules the next future occurrence.
    /// </summary>
    /// <remarks>
    /// Use this when running late is worse than not running at all, for example "refresh a
    /// live dashboard snapshot" or "poll current state" jobs where stale work has no value.
    /// The skipped occurrence is recorded as a scheduler outcome with reason <c>misfire</c>.
    /// </remarks>
    Skip,

    /// <summary>
    /// Runs missed occurrences in due-time order until the scheduler catches up or reaches the
    /// configured catch-up cap.
    /// </summary>
    /// <remarks>
    /// Use this only when each missed slot represents meaningful work, such as processing
    /// time-bucketed aggregates. Catch-up is bounded by
    /// <see cref="SchedulerOptions.MaxMisfireCatchUpRuns"/> to prevent unbounded bursts after
    /// long pauses.
    /// </remarks>
    CatchUp
}
