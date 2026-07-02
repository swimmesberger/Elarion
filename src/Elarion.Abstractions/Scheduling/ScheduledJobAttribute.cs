namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Marks a method as a compile-time scheduled job or a class as a runtime-schedulable job type.
/// </summary>
/// <remarks>
/// Methods must declare exactly one of <see cref="FixedRate"/>, <see cref="FixedDelay"/>, or
/// <see cref="Cron"/>; runtime-schedulable classes may declare at most one. All string values
/// also accept <c>${Config:Key}</c> and <c>${Config:Key:-default}</c> placeholders, which are
/// re-resolved against configuration for every occurrence:
/// <code>
/// [ScheduledJob("billing.daily", Cron = "0 0 3 * * *", TimeZone = "Europe/Vienna")]
/// [ScheduledJob("mailbox.poll", FixedDelay = "${Mailbox:PollingInterval:-15m}", Enabled = "${Mailbox:Enabled:-false}")]
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScheduledJobAttribute(string name) : Attribute {
    /// <summary>The stable unique job name used in logs, telemetry, and runtime scheduling.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// Grid-aligned interval between due times, regardless of how long runs take. Supports
    /// invariant <see cref="TimeSpan"/> text or compact suffixes such as <c>50ms</c>,
    /// <c>30s</c>, <c>15m</c>, <c>6h</c>, and <c>1d</c>.
    /// </summary>
    /// <remarks>
    /// Fixed-rate jobs keep their schedule anchored to the prior due time. A slow or late run
    /// therefore does not shift the schedule forward; missed slots are handled by
    /// <see cref="MisfirePolicy"/> and overlapping active runs by <see cref="Overlap"/>.
    /// </remarks>
    public string? FixedRate { get; init; }

    /// <summary>
    /// Delay between the completion of one run and the start of the next. Uses the same
    /// duration format as <see cref="FixedRate"/>.
    /// </summary>
    /// <remarks>
    /// Fixed-delay jobs are useful for polling loops because each pass waits for the previous
    /// pass to finish before the delay starts. Misfire policy does not apply because there is
    /// no independent time grid to catch up or skip.
    /// </remarks>
    public string? FixedDelay { get; init; }

    /// <summary>
    /// Cron expression with five (minute-level) or six (second-level) fields, e.g.
    /// <c>"0 0 3 * * *"</c> for daily at 03:00. Evaluated in <see cref="TimeZone"/>.
    /// </summary>
    /// <remarks>
    /// Use <c>"-"</c> to disable a cron schedule, typically through a placeholder such as
    /// <c>${Jobs:DailyCron:--}</c>. Cron schedules are grid-based, so missed occurrences use
    /// <see cref="MisfirePolicy"/>.
    /// </remarks>
    public string? Cron { get; init; }

    /// <summary>
    /// Time zone id used to evaluate <see cref="Cron"/>; defaults to UTC.
    /// </summary>
    /// <remarks>
    /// The value is passed to <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/> after
    /// placeholder resolution. Prefer IANA ids such as <c>Europe/Vienna</c> on Linux/macOS.
    /// </remarks>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Optional delay before the first execution. Uses the same format as <see cref="FixedRate"/>.
    /// Not valid with <see cref="Cron"/>.
    /// </summary>
    public string? InitialDelay { get; init; }

    /// <summary>
    /// Whether interval jobs run once immediately when the host starts (default true).
    /// Not valid with <see cref="Cron"/>.
    /// </summary>
    public bool RunOnStart { get; init; } = true;

    /// <summary>Optional key used to serialize jobs that must not run concurrently with each other.</summary>
    /// <remarks>
    /// Jobs with the same non-empty group share a serialization gate even when they have
    /// different job names. Use this for shared external resources such as one mailbox or
    /// one third-party API quota.
    /// </remarks>
    public string? Group { get; init; }

    /// <summary>
    /// Controls how occurrences of the same job behave when another occurrence is already active.
    /// </summary>
    public ScheduledJobOverlap Overlap { get; init; } = ScheduledJobOverlap.Skip;

    /// <summary>
    /// Controls how fixed-rate and cron schedules handle missed in-process occurrences.
    /// Defaults to <see cref="ScheduledJobMisfirePolicy.FireOnce"/> to run one overdue
    /// occurrence and skip intermediate slots.
    /// </summary>
    public ScheduledJobMisfirePolicy MisfirePolicy { get; init; } = ScheduledJobMisfirePolicy.FireOnce;

    /// <summary>
    /// Maximum concurrently executing occurrences for this job when concurrent execution is allowed.
    /// <c>0</c> means no job-local cap; the global scheduler concurrency limit still applies.
    /// </summary>
    /// <remarks>
    /// This setting is only meaningful when <see cref="Overlap"/> is
    /// <see cref="ScheduledJobOverlap.AllowConcurrent"/>. Values below zero are rejected by
    /// the source generator.
    /// </remarks>
    public int MaxConcurrentRuns { get; init; }

    /// <summary>
    /// Whether the job runs: <c>"true"</c>, <c>"false"</c>, or a placeholder such as
    /// <c>"${Modules:Invoicing:Enabled}"</c>. Evaluated per occurrence; an unconfigured
    /// placeholder without an inline default means enabled, an unparseable value means
    /// disabled. Omit for always enabled.
    /// </summary>
    public string? Enabled { get; init; }

    /// <summary>
    /// Where occurrences execute on a multi-node deployment: <see cref="JobPlacement.Cluster"/> (default —
    /// exactly one node per occurrence when a cross-instance coordinator is registered) or
    /// <see cref="JobPlacement.EveryNode"/> (every node runs every occurrence — for jobs that maintain
    /// process-local in-memory state, which coordination would leave stale on the losing nodes).
    /// Single-node behavior is identical for both values.
    /// </summary>
    public JobPlacement Placement { get; init; } = JobPlacement.Cluster;
}
