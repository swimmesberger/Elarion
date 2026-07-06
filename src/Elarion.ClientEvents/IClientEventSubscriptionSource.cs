using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents;

/// <summary>
/// The transport-side subscribe seam: a connection (e.g. one SSE request) registers the exact
/// (topic, scope) pairs it may observe and drains the returned handle until the client disconnects.
/// Authorization happens <em>before</em> this call — the source matches envelopes to subscriptions and
/// enforces nothing.
/// </summary>
public interface IClientEventSubscriptionSource {
    /// <summary>Registers a subscriber for the given (topic, scope) pairs.</summary>
    /// <param name="subscriptions">The already-authorized subscriptions; envelopes match on exact
    /// topic + scope equality.</param>
    /// <returns>The handle to drain; disposing it removes the subscriber.</returns>
    ClientEventSubscriptionHandle Subscribe(IReadOnlyList<ClientEventSubscription> subscriptions);
}
