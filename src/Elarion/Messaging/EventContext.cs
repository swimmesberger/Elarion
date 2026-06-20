using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// The runtime <see cref="IEventContext{TEvent}"/> instance created per publish. A single context
/// is shared by every consumer of one dispatch, so consumers may declare either the non-generic
/// <see cref="IEventContext"/> or the strongly typed <see cref="IEventContext{TEvent}"/>.
/// </summary>
internal sealed class EventContext<TEvent>(TEvent message, Guid correlationId, EventPlane plane)
    : IEventContext<TEvent> {
    public TEvent Message { get; } = message;

    public Guid CorrelationId { get; } = correlationId;

    public EventPlane Plane { get; } = plane;

    object IEventContext.Message => Message!;
}
