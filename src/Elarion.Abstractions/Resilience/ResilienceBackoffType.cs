namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Describes how retry delays grow between attempts.
/// </summary>
public enum ResilienceBackoffType {
    /// <summary>
    /// Reuses the configured base delay for every retry attempt.
    /// </summary>
    /// <remarks>
    /// With <c>Delay = "10s"</c>, every retry waits about ten seconds, plus jitter when enabled.
    /// </remarks>
    Constant,

    /// <summary>
    /// Multiplies the configured base delay by the completed attempt number.
    /// </summary>
    /// <remarks>
    /// With <c>Delay = "10s"</c>, retry delays are about 10s, 20s, 30s, and so on, capped by
    /// <see cref="ResiliencePolicyAttribute.MaxDelay"/> when configured.
    /// </remarks>
    Linear,

    /// <summary>
    /// Grows the configured base delay exponentially by retry attempt.
    /// </summary>
    /// <remarks>
    /// With <c>Delay = "10s"</c>, retry delays are about 10s, 20s, 40s, and so on, capped by
    /// <see cref="ResiliencePolicyAttribute.MaxDelay"/> when configured.
    /// </remarks>
    Exponential
}
