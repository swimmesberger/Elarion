namespace Elarion.Connections.AspNetCore;

/// <summary>
/// Per-connection overrides of the endpoint's options, returned by
/// <see cref="WebSocketConnectionHandler.ConfigureConnectionAsync"/> — every property is optional and
/// <see langword="null"/> inherits the endpoint value. This is how one route serves connection tiers with
/// different limits, keepalive cadences, or transport tags (e.g. device families distinguished by route
/// value, query, or header): resolve the binding configuration from the upgrade request and return its
/// settings. WebSocket frames messages natively, so unlike the TCP sibling there is no framer to choose.
/// </summary>
public sealed record WebSocketConnectionSettings {
    /// <summary>This connection's message size cap.</summary>
    public int? MaxMessageBytes { get; init; }

    /// <summary>This connection's idle window for the codec's <c>OnIdleAsync</c>.</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>This connection's transport-level keep-alive ping interval.</summary>
    public TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>This connection's transport tag — keep it a <b>bounded</b> vocabulary (a tier or
    /// protocol-family name, never a device id).</summary>
    public string? Transport { get; init; }
}
