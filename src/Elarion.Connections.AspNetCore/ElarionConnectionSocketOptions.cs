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
    /// Protocol-level keepalives (a device poll cadence) are the codec's business, not this knob.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; }
}
