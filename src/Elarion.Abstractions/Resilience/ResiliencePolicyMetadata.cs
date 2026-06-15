namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Framework-owned metadata for a named resilience policy.
/// </summary>
/// <remarks>
/// Generated policy registration stores this implementation-neutral metadata. Runtime
/// adapters can turn it into executable pipelines, and scheduler-deferred retry can compute
/// attempts and due times from it directly.
/// </remarks>
public sealed record ResiliencePolicyMetadata {
    /// <summary>
    /// Stable policy name used by handlers, scheduled jobs, generated references, and runtime adapters.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Retry strategy metadata when the policy includes retry; null for timeout-only policies.
    /// </summary>
    public ResilienceRetryOptions? Retry { get; init; }

    /// <summary>
    /// Per-attempt timeout duration when the policy includes timeout; null when no timeout is configured.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
