using System.Net;

namespace Elarion.Connections.Tcp;

/// <summary>Shared tuning for TCP connection endpoints (listener and dialer).</summary>
public class ElarionTcpConnectionOptions {
    /// <summary>The framing that turns the byte stream into messages — required. Ship-with:
    /// <see cref="LengthPrefixedTcpFramer"/> and <see cref="DelimitedTcpFramer"/>.</summary>
    public TcpMessageFramer? Framer { get; set; }

    /// <summary>The maximum unconsumed buffer (≈ largest message) before the connection is closed
    /// (default 1 MiB). Bulk payloads belong in the staged-blob tier, not a frame.</summary>
    public int MaxMessageBytes { get; set; } = 1024 * 1024;

    /// <summary>See the WebSocket adapter's equivalent: when set, the codec's <c>OnIdleAsync</c> fires per
    /// elapsed window without inbound traffic — the protocol-keepalive/dead-link hook.</summary>
    public TimeSpan? IdleTimeout { get; set; }

    /// <summary>How long the handshake may take before the socket is dropped (default 10 s) — an accepted
    /// client that never authenticates must not hold a slot forever.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The transport tag stamped on connections (default <c>"tcp"</c>) — bounded telemetry
    /// vocabulary, never a behavioral switch.</summary>
    public string Transport { get; set; } = "tcp";
}

/// <summary>Options for a listening endpoint (devices dial in).</summary>
public sealed class ElarionTcpListenerOptions : ElarionTcpConnectionOptions {
    /// <summary>Where to listen — required. Port 0 binds dynamically; observe the bound endpoint via
    /// <see cref="OnListening"/>.</summary>
    public IPEndPoint? ListenEndPoint { get; set; }

    /// <summary>Called once with the actually bound endpoint after the listener starts (dynamic-port
    /// discovery for tests and self-registration).</summary>
    public Action<IPEndPoint>? OnListening { get; set; }
}

/// <summary>Options for a dial-out endpoint (the gateway initiates the connection to the device).</summary>
public sealed class ElarionTcpDialerOptions : ElarionTcpConnectionOptions {
    /// <summary>The remote host to dial — required.</summary>
    public string? Host { get; set; }

    /// <summary>The remote port to dial — required.</summary>
    public int Port { get; set; }

    /// <summary>The first-retry delay of the jittered exponential reconnect backoff (default 1 s).</summary>
    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The backoff ceiling (default 30 s).</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
