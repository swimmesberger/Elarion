using Elarion.Abstractions.Resilience;

namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Runtime scheduling options for one-time in-memory jobs.
/// </summary>
/// <remarks>
/// These options apply only to jobs scheduled through <see cref="IJobScheduler"/>. Compile-time
/// recurring jobs use metadata from <see cref="ScheduledJobAttribute"/> and optional
/// <see cref="ResilientAttribute"/>.
/// </remarks>
public sealed record ScheduledJobOptions {
    /// <summary>
    /// Optional named resilience policy to apply to this runtime job.
    /// Inline mode executes the generated resilience pipeline directly; deferred mode uses generated retry metadata
    /// to schedule future attempts while still applying per-attempt timeout behavior.
    /// </summary>
    /// <remarks>
    /// When <see cref="ResilienceMode"/> is <see cref="ScheduledJobResilienceMode.DeferredRetry"/>,
    /// the policy must have generated metadata so the scheduler can compute future retry due
    /// times without sleeping inside a resilience pipeline.
    /// </remarks>
    public ResiliencePolicyReference? ResiliencePolicy { get; init; }

    /// <summary>
    /// How the resilience policy is applied to this runtime job.
    /// Use inline mode when the current scheduler run should own all attempts; use deferred retry when another handler
    /// needs to observe <see cref="ScheduledJobState"/> by logical job id between attempts.
    /// </summary>
    public ScheduledJobResilienceMode ResilienceMode { get; init; } = ScheduledJobResilienceMode.Inline;

    /// <summary>
    /// Optional caller-provided value surfaced in <see cref="ScheduledJobState"/> and scheduler telemetry.
    /// </summary>
    /// <remarks>
    /// Use this to connect an HTTP/RPC request, domain operation, or aggregate id to the
    /// background job without changing the scheduler's own identifiers.
    /// </remarks>
    public string? CorrelationId { get; init; }
}
