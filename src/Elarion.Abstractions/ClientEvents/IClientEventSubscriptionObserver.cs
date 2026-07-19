namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// The producer-side subscription lifecycle for one topic: "this is your new subscriber" and "someone is /
/// no one is watching". Declared per topic (<c>[SubscriptionObserver&lt;T&gt;]</c> on the contract, or
/// <c>ObserveSubscriptions&lt;T&gt;()</c> on the topic options) — implementations are resolved from a fresh
/// DI scope per callback, so they inject facades/services like any scoped code. Both methods default to
/// no-ops; implement what the producer needs.
/// </summary>
/// <remarks>
/// The semantics are deliberately the proven Rx ones, minus backpressure (client events stay a hot
/// at-most-once stream): <see cref="OnSubscribedAsync"/> is the <c>BehaviorSubject</c>-style greeting with
/// the producer in control — fetch the current value (a normal request/reply turn on the owning actor) and
/// emit it to the sink, or don't; <see cref="OnInterestChangedAsync"/> is <c>RefCount</c> with a disconnect
/// delay — <see langword="true"/> on the first subscriber of a (topic, scope), <see langword="false"/> only
/// after the last one leaves <b>and</b> the linger elapses, so a browser reload never bounces an upstream
/// connection. Callbacks run on the node holding the subscription (see <see cref="IClientEventInterest"/>
/// for why that is the producer's node in the recommended topology), off the subscribe path — a slow
/// observer never delays the stream, and a thrown exception is logged, never surfaced to the client.
/// </remarks>
public interface IClientEventSubscriptionObserver {
    /// <summary>A new subscriber attached; <paramref name="sink"/> delivers to that subscriber only.
    /// Called once per subscribed (topic, scope) pair of a connection.</summary>
    /// <param name="subscription">The attached subscription.</param>
    /// <param name="sink">The single-subscriber sink (e.g. for an initial value).</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask OnSubscribedAsync(
        ClientEventSubscription subscription, IClientEventSubscriberSink sink, CancellationToken ct) {
        return default;
    }

    /// <summary>Interest in a (topic, scope) transitioned: <see langword="true"/> on the first subscriber,
    /// <see langword="false"/> after the last one left and the topic's linger elapsed.</summary>
    /// <param name="subscription">The (topic, scope) whose interest changed.</param>
    /// <param name="active">Whether at least one subscriber now observes the pair.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask OnInterestChangedAsync(
        ClientEventSubscription subscription, bool active, CancellationToken ct) {
        return default;
    }
}
