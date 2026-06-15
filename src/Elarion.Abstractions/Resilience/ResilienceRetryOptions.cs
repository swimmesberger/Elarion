namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Framework-owned retry metadata derived from a generated resilience policy.
/// </summary>
/// <remarks>
/// This metadata describes generated retry behavior independently of the runtime that will
/// execute it. The scheduler uses it for deferred retry because it needs to calculate future
/// due times without sleeping inside an executing pipeline.
/// </remarks>
public sealed record ResilienceRetryOptions {
    /// <summary>
    /// Maximum retry attempts after the original attempt.
    /// </summary>
    /// <remarks>
    /// Total possible attempts equals this value plus one.
    /// </remarks>
    public required int MaxRetryAttempts { get; init; }

    /// <summary>
    /// Base retry delay before applying <see cref="Backoff"/>, <see cref="MaxDelay"/>, and
    /// <see cref="UseJitter"/>.
    /// </summary>
    public required TimeSpan Delay { get; init; }

    /// <summary>How retry delays grow between attempts.</summary>
    public required ResilienceBackoffType Backoff { get; init; }

    /// <summary>
    /// Optional maximum retry delay after backoff calculation.
    /// </summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>
    /// Whether retry delays should include random jitter.
    /// </summary>
    public bool UseJitter { get; init; }
}
