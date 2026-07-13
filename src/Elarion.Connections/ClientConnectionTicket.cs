using System.Collections.ObjectModel;
using System.Security.Claims;

namespace Elarion.Connections;

/// <summary>
/// The outcome of a successful connection handshake, produced by the app's authenticator: the principal the
/// link acts as, its stable id, and adapter metadata to stamp onto the <c>ClientConnection</c>. Transport
/// neutral — the WebSocket adapter and any future socket adapter consume the same ticket.
/// </summary>
public sealed record ClientConnectionTicket {
    /// <summary>The authenticated principal — a user for browser links, a device identity for device links.</summary>
    public required ClaimsPrincipal Principal { get; init; }

    /// <summary>The stable principal id (user id / device id) the registry indexes by; see
    /// <c>ClientConnection.PrincipalId</c>.</summary>
    public string? PrincipalId { get; init; }

    /// <summary>Opaque annotations stamped onto the connection (e.g. <c>channel=telemetry</c>, firmware
    /// version). The foundation never reads them.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}
