using Elarion.ClientEvents;

namespace Elarion.Connections;

/// <summary>
/// Delivers one client-event envelope to a connection's client — implemented by the transport adapter,
/// which owns the framing (a SignalR adapter maps it onto a hub method, a socket adapter onto its own
/// frame; the payload is already canonical JSON text, so no adapter re-serializes the contract). Throw
/// <see cref="Elarion.Abstractions.Connections.ClientConnectionClosedException"/> (or any exception) to end
/// the subscription; delivery is at-most-once like every client event.
/// </summary>
/// <param name="envelope">The envelope to frame and send.</param>
/// <param name="ct">Cancelled when the subscription is disposed or its connection ends.</param>
public delegate ValueTask ClientEventDelivery(ClientEventEnvelope envelope, CancellationToken ct);
