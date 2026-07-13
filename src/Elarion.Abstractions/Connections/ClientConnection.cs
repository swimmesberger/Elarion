using System.Collections.ObjectModel;
using System.Security.Claims;

namespace Elarion.Abstractions.Connections;

/// <summary>
/// The transport-neutral identity of one live bidirectional client connection: an opaque id, the principal
/// captured at connect, and adapter-owned metadata. It deliberately carries no transport handle — no socket,
/// hub context, or datagram endpoint — so everything written against it stays portable across adapters
/// (SignalR, WebSocket, TCP, UDP, …).
/// </summary>
/// <remarks>
/// <para>
/// The adapter authenticates <b>once at connect</b> (its handshake), captures the result here, and seeds it
/// into every per-message dispatch scope via <see cref="Dispatch.DispatchScopeContext"/> — the same rail the
/// JSON-RPC and MCP transports use per call. Authorization still evaluates per dispatch against these
/// claims; terminating the connection on credential expiry is adapter policy.
/// </para>
/// <para>
/// A connection is <b>node-local state</b>: the record describes a link this instance holds. Cross-node
/// addressing composes from single-homed actors and the role-holder proxy, never from replicating these
/// records.
/// </para>
/// </remarks>
public sealed record ClientConnection {
    /// <summary>
    /// Opaque adapter-minted identifier, unique on this node for the connection's lifetime. Consumers treat
    /// it as a routing key only; no format is promised.
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// The adapter's short name (for example <c>"signalr"</c> or <c>"tcp"</c>) — a bounded telemetry tag,
    /// never a behavioral switch. Code that branches on it is hosting two conversations and should say so
    /// with two code paths at the boundary, not transport sniffing downstream.
    /// </summary>
    public required string Transport { get; init; }

    /// <summary>
    /// The principal authenticated at connect. Seeded into each dispatch scope so <c>ICurrentUser</c> and the
    /// authorization pipeline see the connection's caller on every message.
    /// </summary>
    public required ClaimsPrincipal Principal { get; init; }

    /// <summary>
    /// The stable identifier of <see cref="Principal"/> — a user id for browser links, a device id for
    /// device links — resolved by the adapter at the boundary (the one place that knows the identity
    /// mapping) so <see cref="IClientConnectionRegistry"/> can index without claims knowledge. Many
    /// connections share one principal id (browser tabs; a device's parallel channels — critical,
    /// visualization, logging — each register as their own connection under the device's id).
    /// <see langword="null"/> when the link has no stable identity.
    /// </summary>
    public string? PrincipalId { get; init; }

    /// <summary>When the connection was established.</summary>
    public required DateTimeOffset ConnectedAt { get; init; }

    /// <summary>
    /// Opaque adapter-owned annotations (negotiated protocol version, remote endpoint, …). The foundation
    /// never reads these; anything the foundation must understand is a first-class property instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
