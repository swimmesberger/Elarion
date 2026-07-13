using System.Net;

namespace Elarion.Connections.Tcp;

/// <summary>The peer identity available before any byte is exchanged — what a binding-configuration lookup
/// keys on when the same endpoint serves differently-configured devices.</summary>
public readonly record struct TcpConnectionPeer(EndPoint? RemoteEndPoint, EndPoint? LocalEndPoint);

/// <summary>
/// Per-connection overrides of the endpoint's options, returned by
/// <see cref="TcpConnectionHandler.ConfigureConnectionAsync"/> — every property is optional and
/// <see langword="null"/> inherits the endpoint value. This is how one listening endpoint serves devices
/// with different wire framings, size limits, or keepalive cadences: resolve the peer's binding
/// configuration and return its settings.
/// </summary>
public sealed record TcpConnectionSettings {
    /// <summary>This connection's framing (e.g. a vendor telegram framer for one device family, length
    /// prefixes for another). Applies to the handshake too — it is chosen before any byte is read.</summary>
    public TcpMessageFramer? Framer { get; init; }

    /// <summary>This connection's message size cap.</summary>
    public int? MaxMessageBytes { get; init; }

    /// <summary>This connection's idle window for the codec's <c>OnIdleAsync</c> (device families differ
    /// in poll cadence).</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>This connection's handshake deadline.</summary>
    public TimeSpan? HandshakeTimeout { get; init; }

    /// <summary>This connection's Nagle setting; see the endpoint option of the same name.</summary>
    public bool? NoDelay { get; init; }

    /// <summary>This connection's transport tag — keep it a <b>bounded</b> vocabulary (a protocol-family
    /// name, never a device id).</summary>
    public string? Transport { get; init; }
}
