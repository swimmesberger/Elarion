namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Delivers events to exactly <b>one</b> subscriber — the one whose arrival triggered
/// <see cref="IClientEventSubscriptionObserver.OnSubscribedAsync"/>. The producer-controlled "initial value"
/// primitive (the <c>BehaviorSubject</c> greeting, with the producer deciding): emit the current value so the
/// new subscriber starts converged, without re-broadcasting it to everyone already subscribed.
/// </summary>
/// <remarks>
/// The sink is bound to the subscription's (topic, scope): the event type must belong to that topic
/// (anything else throws), and delivery is best-effort at-most-once like every client event — if the
/// subscriber disconnected in the meantime, the publish is a no-op. Emissions race with live publishes, so
/// payload contracts keep carrying a monotonic version and clients keep their apply guard.
/// </remarks>
public interface IClientEventSubscriberSink {
    /// <summary>The subscription this sink delivers to.</summary>
    ClientEventSubscription Subscription { get; }

    /// <summary>Delivers <paramref name="event"/> to this subscriber only.</summary>
    /// <typeparam name="TEvent">The client-event contract; must be the subscribed topic's contract.</typeparam>
    /// <param name="event">The event to deliver.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class, IClientEvent;
}
