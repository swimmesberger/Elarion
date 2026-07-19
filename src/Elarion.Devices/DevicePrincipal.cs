using System.Security.Claims;

namespace Elarion.Devices;

/// <summary>
/// The device <see cref="ClaimsPrincipal"/> factory — a stable claim shape so device-initiated
/// dispatches flow through <c>ICurrentUser</c>, <c>[RequirePermission]</c>, and auditing unchanged.
/// The device id doubles as the name identifier, matching the connection registry's
/// "<c>PrincipalId</c> = device id for device links" convention (ADR-0053).
/// </summary>
public static class DevicePrincipal {
    /// <summary>The identity's <see cref="ClaimsIdentity.AuthenticationType"/>.</summary>
    public const string AuthenticationType = "ElarionDevice";

    /// <summary>The claim carrying the device id.</summary>
    public const string DeviceIdClaimType = "elarion:device";

    /// <summary>Creates the principal for an authenticated device.</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="additionalClaims">Optional extra claims (e.g. permission claims for device-initiated commands).</param>
    public static ClaimsPrincipal Create(string deviceId, IEnumerable<Claim>? additionalClaims = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var claims = new List<Claim> {
            new(DeviceIdClaimType, deviceId),
            new(ClaimTypes.NameIdentifier, deviceId),
            new(ClaimTypes.Name, deviceId)
        };
        if (additionalClaims is not null) claims.AddRange(additionalClaims);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    /// <summary>Whether the principal was authenticated as a device.</summary>
    /// <param name="principal">The principal to inspect.</param>
    public static bool IsDevice(ClaimsPrincipal principal) {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identities.Any(identity =>
            identity.AuthenticationType == AuthenticationType &&
            identity.HasClaim(claim => claim.Type == DeviceIdClaimType));
    }

    /// <summary>The device id, or <see langword="null"/> for non-device principals.</summary>
    /// <param name="principal">The principal to inspect.</param>
    public static string? GetDeviceId(ClaimsPrincipal principal) {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirst(DeviceIdClaimType)?.Value;
    }
}
