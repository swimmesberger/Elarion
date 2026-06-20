using Elarion.Abstractions.Messaging;

namespace Elarion.Messaging;

/// <summary>
/// In-process <see cref="IIntegrationEventBus"/>: records integration events into the scope's
/// buffer for after-commit delivery rather than delivering them inline.
/// </summary>
internal sealed class InMemoryIntegrationEventBus(EventDispatchScope scope) : IIntegrationEventBus {
    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent {
        ArgumentNullException.ThrowIfNull(@event);

        var context = new EventContext<TEvent>(@event, Guid.NewGuid(), EventPlane.Integration);
        scope.Add(new EventEnvelope(@event, typeof(TEvent), context));
        return ValueTask.CompletedTask;
    }
}
