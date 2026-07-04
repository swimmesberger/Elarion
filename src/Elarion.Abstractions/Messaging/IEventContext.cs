namespace Elarion.Abstractions.Messaging;

/// <summary>
/// Carries metadata for a single event consumer invocation.
/// </summary>
/// <remarks>
/// A consumer may declare an <see cref="IEventContext"/> (or <see cref="IEventContext{TEvent}"/>)
/// parameter to receive the correlation identifier and the message instance without re-declaring the
/// message parameter. The concrete context is owned by the event bus runtime, so runtime-specific
/// state does not leak into the consumer-authoring API.
/// </remarks>
public interface IEventContext {
    /// <summary>
    /// A stable identifier that ties together all consumer invocations triggered by a single publish.
    /// </summary>
    /// <remarks>
    /// The correlation identifier flows across the after-commit boundary so integration-event
    /// consumers can be correlated back to the originating command. It is a <b>tracing</b> identifier —
    /// do not key deduplication on it; use <see cref="MessageId"/>.
    /// </remarks>
    Guid CorrelationId { get; }

    /// <summary>
    /// The durable identity of the delivered message, stable across redeliveries of the same message —
    /// for the EF Core outbox, the outbox row's id. <see langword="null"/> on the domain plane, which
    /// dispatches inline in the publisher's transaction and has no message.
    /// </summary>
    /// <remarks>
    /// This is the deduplication key (ADR-0022): the inbox claims it per consumer, and a consumer calling a
    /// downstream system that dedups on a caller-supplied key (a payment API's idempotency key, for example)
    /// should pass this value so a redelivery collapses at the recipient too.
    /// </remarks>
    Guid? MessageId { get; }

    /// <summary>The plane on which this message is being dispatched.</summary>
    EventPlane Plane { get; }

    /// <summary>The boxed message instance being dispatched.</summary>
    object Message { get; }
}

/// <summary>
/// A strongly typed <see cref="IEventContext"/> that exposes the message instance.
/// </summary>
/// <typeparam name="TEvent">The message type being dispatched.</typeparam>
public interface IEventContext<out TEvent> : IEventContext {
    /// <summary>The message instance being dispatched.</summary>
    new TEvent Message { get; }
}
