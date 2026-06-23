namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Thrown when a handler-form event consumer (a <c>[ConsumeEvent]</c> handler whose request is the
/// event) returns a failed <see cref="Result"/>. A handler subscriber has no return channel to the
/// publisher, so the failure is surfaced as an exception, which each backend handles per its plane:
/// the in-memory domain bus aggregates and rethrows (failing the command), the in-memory integration
/// pump logs and isolates it, and the outbox dispatcher lets it propagate to trigger a retry.
/// </summary>
public sealed class EventConsumerFailedException : Exception {
    /// <summary>The application error returned by the failing handler.</summary>
    public AppError Error { get; }

    /// <summary>Creates the exception from the handler's <paramref name="error"/>.</summary>
    public EventConsumerFailedException(AppError error)
        : base($"Event consumer failed: {error.Kind} - {error.Message}") {
        Error = error;
    }
}
