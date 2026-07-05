using System.Diagnostics;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// In-process <see cref="IIntegrationEventBus"/>: records integration events into the scope's
/// buffer for after-commit delivery rather than delivering them inline.
/// </summary>
internal sealed class InMemoryIntegrationEventBus(EventDispatchScope scope) : IIntegrationEventBus {
    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent {
        ArgumentNullException.ThrowIfNull(@event);

        EventTelemetry.RecordPublish(typeof(TEvent).Name, EventPlane.Integration);

        // The per-publish message id: the in-memory tier delivers each envelope once, but assigning the id keeps
        // the consumer-visible contract (IEventContext.MessageId, the inbox key) identical to the outbox tier.
        var context = new EventContext<TEvent>(@event, Guid.CreateVersion7(), EventPlane.Integration, messageId: Guid.CreateVersion7());
        // Capture the publisher's trace context now — delivery happens after commit on a pump thread
        // where Activity.Current is long gone.
        scope.Add(new EventEnvelope(@event, typeof(TEvent), context, Activity.Current?.Context ?? default));
        return ValueTask.CompletedTask;
    }
}
