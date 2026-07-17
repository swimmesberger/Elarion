using System.Collections.ObjectModel;
using System.Security.Claims;

namespace Elarion.Abstractions.Connections;

/// <summary>
/// The transport-neutral snapshot of one live bidirectional client connection: stable connection facts and
/// one immutable, revisioned identity. It deliberately carries no transport handle — no socket, hub context,
/// or datagram endpoint — so everything written against it stays portable across adapters (SignalR,
/// WebSocket, TCP, UDP, …).
/// </summary>
/// <remarks>
/// <para>
/// An adapter may register an anonymous initial snapshot and later promote it exactly once through
/// <see cref="IClientConnectionRegistry.PromoteAsync"/>. Promotion replaces the principal, principal id,
/// metadata, and <see cref="IdentityRevision"/> together; it never mutates a snapshot already captured by a
/// dispatch. The adapter captures <see cref="IClientConnectionSink.Connection"/> once at each message boundary
/// and seeds it into the per-message dispatch scope via <see cref="Dispatch.DispatchScopeContext"/>. A dispatch
/// already in flight therefore keeps its original identity while the next dispatch observes a successful
/// promotion. Terminating the connection on credential expiry remains adapter policy.
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
    /// The principal in this identity snapshot. It may be anonymous at registration and is replaced, never
    /// mutated in place, by a successful promotion.
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
    /// <remarks>
    /// User ids and device ids share this <b>one namespace</b> — the registry indexes on the string alone.
    /// A custom device-id scheme must therefore never be able to produce a value that collides with a user
    /// id (a collision would let a device's connections be addressed as that user, and vice versa); the
    /// default minted v7-GUID device ids are safe by construction.
    /// </remarks>
    public string? PrincipalId { get; init; }

    /// <summary>When the connection was established.</summary>
    public required DateTimeOffset ConnectedAt { get; init; }

    /// <summary>
    /// Monotonically increasing identity revision. Adapters create the initial snapshot at revision zero;
    /// the supported anonymous-to-authenticated promotion commits revision one.
    /// </summary>
    public long IdentityRevision { get; init; }

    /// <summary>
    /// Opaque adapter-owned identity annotations (negotiated protocol version, authentication method, …).
    /// The registry defensively copies and bounds them at registration and promotion; anything the foundation
    /// must understand is a first-class property instead.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
