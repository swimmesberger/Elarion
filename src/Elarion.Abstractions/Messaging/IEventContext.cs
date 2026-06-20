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
    /// consumers can be correlated back to the originating command.
    /// </remarks>
    Guid CorrelationId { get; }

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
