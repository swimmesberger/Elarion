namespace Elarion.Abstractions.Resilience;

/// <summary>
/// Marks a failure as terminal even when the active resilience policy would otherwise retry.
/// </summary>
/// <remarks>
/// Throw this from handler or job code when retrying would be harmful or pointless, for
/// example validation failures, missing domain state, or non-idempotent side effects that
/// already completed.
/// </remarks>
public sealed class NonRetryableException : Exception {
    /// <summary>Creates a terminal resilience failure.</summary>
    public NonRetryableException(string message) : base(message) {
    }

    /// <summary>Creates a terminal resilience failure with an inner exception.</summary>
    public NonRetryableException(string message, Exception innerException) : base(message, innerException) {
    }
}
