namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Marks a static partial type as a source-generated resilience policy definition.
/// </summary>
/// <remarks>
/// A policy is a named execution behavior that can be applied to generated handlers and scheduled jobs.
/// Retry options create additional attempts after a handled exception. <see cref="Timeout"/> limits each
/// individual attempt and cancels the attempt token; operation code must observe the cancellation token for
/// the underlying work to stop promptly. The timeout is not a total deadline across all retries.
/// </remarks>
/// <param name="name">Stable policy name used by handlers, jobs, and generated registration.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ResiliencePolicyAttribute(string name) : Attribute {
    /// <summary>
    /// Stable policy name used by <see cref="ResilientAttribute"/> and
    /// <see cref="Elarion.Abstractions.Scheduling.ScheduledJobOptions.ResiliencePolicy"/>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Maximum retry attempts after the original attempt. Supplying this or another retry
    /// property enables retry generation.
    /// </summary>
    /// <remarks>
    /// <c>0</c> means no retries. <c>3</c> means up to four total attempts: the original
    /// attempt plus three retries.
    /// </remarks>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay before a retry attempt, e.g. <c>200ms</c>, <c>2s</c>, <c>5m</c>, or invariant <see cref="TimeSpan"/> text.
    /// Inline execution waits inside the current invocation; scheduler-deferred retry re-enqueues a future attempt.
    /// </summary>
    /// <remarks>
    /// The effective delay may be changed by <see cref="Backoff"/>, capped by <see cref="MaxDelay"/>,
    /// and randomized by <see cref="UseJitter"/>.
    /// </remarks>
    public string Delay { get; init; } = "2s";

    /// <summary>
    /// How retry delays grow between attempts.
    /// </summary>
    public ResilienceBackoffType Backoff { get; init; } = ResilienceBackoffType.Constant;

    /// <summary>
    /// Optional maximum retry delay after applying <see cref="Backoff"/>.
    /// </summary>
    /// <remarks>
    /// Use this with linear or exponential backoff to prevent long retry waits.
    /// </remarks>
    public string? MaxDelay { get; init; }

    /// <summary>
    /// Whether retry delays should include jitter.
    /// </summary>
    /// <remarks>
    /// Jitter randomizes retry delays so many failed jobs or requests do not retry at the
    /// exact same instant.
    /// </remarks>
    public bool UseJitter { get; init; }

    /// <summary>
    /// Optional per-attempt timeout duration, e.g. <c>30s</c> or invariant <see cref="TimeSpan"/> text.
    /// This is not a total deadline across all retries.
    /// </summary>
    /// <remarks>
    /// With <c>MaxRetryAttempts = 4</c> and <c>Timeout = "30s"</c>, the operation can run for
    /// more than 30 seconds overall because each of the five total attempts receives its own
    /// 30-second timeout window plus retry delays.
    /// </remarks>
    public string? Timeout { get; init; }
}
