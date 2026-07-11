namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Declares the <see cref="IClientEventSubscriptionObserver"/> for this <see cref="IClientEvent"/>
/// contract's topic: <typeparamref name="TObserver"/> is called with a per-subscriber sink when a client
/// subscribes (the producer-controlled initial value) and on debounced first/last interest transitions
/// (lazy compute — start work when someone watches, stop when nobody does). The generator flows it into the
/// topic registration; imperative form: <c>ObserveSubscriptions&lt;TObserver&gt;()</c> on the topic options.
/// </summary>
/// <typeparam name="TObserver">The observer implementation; instantiated from a fresh DI scope per callback.</typeparam>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SubscriptionObserverAttribute<TObserver> : Attribute
    where TObserver : IClientEventSubscriptionObserver {
    /// <summary>How long the last subscriber's departure lingers before
    /// <see cref="IClientEventSubscriptionObserver.OnInterestChangedAsync"/> reports inactive — the
    /// reload-debounce. Defaults to 5 seconds.</summary>
    public double InterestLingerSeconds { get; init; } = 5;
}
