namespace Elarion.ClientEvents;

/// <summary>
/// The single-node default <see cref="IClientEventBroadcaster"/>: delivery is this node's subscribers, done.
/// A cross-node backend replaces this registration (and calls <see cref="IClientEventLocalDelivery"/> on the
/// receiving side).
/// </summary>
internal sealed class InProcessClientEventBroadcaster(IClientEventLocalDelivery delivery) : IClientEventBroadcaster {
    public ValueTask BroadcastAsync(ClientEventEnvelope envelope, CancellationToken ct = default) {
        delivery.Deliver(envelope);
        return ValueTask.CompletedTask;
    }
}
