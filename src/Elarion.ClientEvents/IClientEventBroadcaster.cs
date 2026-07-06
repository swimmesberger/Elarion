namespace Elarion.ClientEvents;

/// <summary>
/// The fan-out seam behind <c>IClientEventPublisher</c>: hands a published envelope to every node's local
/// subscribers. The default is in-process only (single-node correct); a cross-node backend (e.g. PostgreSQL
/// <c>LISTEN/NOTIFY</c>) replaces this registration and must also deliver locally via
/// <see cref="IClientEventLocalDelivery"/> on each receiving node. Past the ~10-node tier, replace the seam
/// with a dedicated broker — never grow this default (ADR-0025/0042).
/// </summary>
public interface IClientEventBroadcaster {
    /// <summary>Fans <paramref name="envelope"/> out to subscribers (on this node, and on every node for a
    /// cross-node implementation).</summary>
    /// <param name="envelope">The published event.</param>
    /// <param name="ct">A cancellation token observed while handing off the envelope.</param>
    ValueTask BroadcastAsync(ClientEventEnvelope envelope, CancellationToken ct = default);
}
