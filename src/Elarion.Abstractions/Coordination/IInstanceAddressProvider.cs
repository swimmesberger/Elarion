namespace Elarion.Abstractions.Coordination;

/// <summary>
/// Supplies this instance's externally reachable base address (e.g. <c>http://10.0.1.5:8080</c>) so a
/// role lease can advertise it — the piece that lets non-holders reach the holder (ADR-0050) without
/// any membership protocol: the address rides the same lease row the holder already renews.
/// </summary>
/// <remarks>
/// Consulted at heartbeat cadence, never on a request path. Implementations are best-effort:
/// returning <see langword="null"/> (address not determinable yet — e.g. the server has not bound its
/// endpoints) simply leaves the lease row's address empty until a later renewal. An explicit
/// configured address (e.g. <c>RoleLeaseOptions.AdvertisedAddress</c>) always wins over this seam —
/// auto-detection is for the happy path (one flat network), configuration is for NAT/proxies/TLS.
/// </remarks>
public interface IInstanceAddressProvider {
    /// <summary>This instance's reachable base address, or <see langword="null"/> when not (yet) known.</summary>
    string? GetInstanceAddress();
}
