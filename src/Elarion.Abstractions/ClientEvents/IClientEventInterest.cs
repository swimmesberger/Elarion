namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// The pull side of subscription interest: whether at least one client on <b>this node</b> currently
/// subscribes to a (topic, scope). Producers use it to skip work nobody observes — an actor's tick checks
/// <c>HasSubscribers</c> before computing/publishing (cheap, race-free by re-check on the next tick).
/// </summary>
/// <remarks>
/// Interest is deliberately <b>node-local</b> (no cross-node presence protocol at this tier). It is
/// authoritative for interest-driven producers because the recommended topology routes their live prefixes —
/// including the events endpoint — to the node that hosts them (the role-holder proxy / the same ingress
/// rule), so their subscribers terminate where they run. Needing aggregated interest without that routing is
/// the replace-the-seam tier. For start/stop transitions (open the upstream socket, stop the poll loop) use
/// <see cref="IClientEventSubscriptionObserver.OnInterestChangedAsync"/> — it debounces the last-subscriber
/// departure so a browser reload does not bounce the producer.
/// </remarks>
public interface IClientEventInterest {
    /// <summary>Whether at least one local subscriber currently observes (<paramref name="topic"/>,
    /// <paramref name="scope"/>). Exact scope match — a <c>Global</c> check does not cover resource scopes.</summary>
    /// <param name="topic">The topic name (e.g. <c>"market.quoteChanged"</c>).</param>
    /// <param name="scope">The exact scope to check.</param>
    bool HasSubscribers(string topic, ClientEventScope scope);
}
