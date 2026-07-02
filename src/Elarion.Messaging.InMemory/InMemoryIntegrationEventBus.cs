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

        var context = new EventContext<TEvent>(@event, Guid.NewGuid(), EventPlane.Integration);
        // Capture the publisher's trace context now — delivery happens after commit on a pump thread
        // where Activity.Current is long gone.
        scope.Add(new EventEnvelope(@event, typeof(TEvent), context, Activity.Current?.Context ?? default));
        return ValueTask.CompletedTask;
    }
}
