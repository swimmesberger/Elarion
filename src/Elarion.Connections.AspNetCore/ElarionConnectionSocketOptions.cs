namespace Elarion.Connections.AspNetCore;

/// <summary>Per-endpoint tuning for <c>MapElarionConnectionSocket</c>.</summary>
public sealed class ElarionConnectionSocketOptions {
    /// <summary>
    /// The maximum reassembled message size (default 1 MiB) — enforced during the handshake and the receive
    /// loop; an oversized message closes the connection with <c>MessageTooBig</c>. Size it to the codec's
    /// largest legitimate frame, not to "large enough": bulk payloads belong in the staged-blob tier.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// The transport-level keep-alive ping interval; <see langword="null"/> uses the server default.
    /// Protocol-level keepalives (a device poll cadence) are the codec's business — see
    /// <see cref="IdleTimeout"/>.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }

    /// <summary>
    /// When set, the codec's <c>OnIdleAsync</c> is called each time this window elapses with no inbound
    /// message — the hook for protocol-level keepalives (send the poll frame there) or dead-link detection
    /// (throw there to close). <see langword="null"/> (the default) never calls it.
    /// </summary>
    public TimeSpan? IdleTimeout { get; set; }
}
