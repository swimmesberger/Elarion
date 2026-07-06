namespace Elarion.ClientEvents;

/// <summary>
/// Delivers an envelope to <em>this node's</em> connected subscribers. The entry point a cross-node
/// <see cref="IClientEventBroadcaster"/> calls when an envelope arrives from another node; the in-process
/// default broadcaster calls it directly.
/// </summary>
public interface IClientEventLocalDelivery {
    /// <summary>Delivers <paramref name="envelope"/> to every matching local subscription. Non-blocking: a
    /// slow subscriber drops its oldest buffered events rather than stalling delivery (hints, not a queue).</summary>
    void Deliver(ClientEventEnvelope envelope);
}
