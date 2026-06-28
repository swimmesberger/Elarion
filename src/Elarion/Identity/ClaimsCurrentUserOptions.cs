using System.Security.Claims;

namespace Elarion.Identity;

/// <summary>
/// Configures how the claims-based <see cref="Abstractions.Identity.ICurrentUser"/> implementation
/// (<see cref="ClaimsPrincipalCurrentUser"/>) maps a <see cref="ClaimsPrincipal"/>'s claims. Transport-neutral
/// — no ASP.NET dependency; any host (HTTP, gRPC, console) can register it via
/// <see cref="ClaimsCurrentUserServiceCollectionExtensions.AddElarionClaimsCurrentUser"/>.
/// </summary>
public sealed class ClaimsCurrentUserOptions {
    /// <summary>Claim type used as the stable user identifier. Defaults to the OIDC <c>"sub"</c> claim.</summary>
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>Claim type used as the user email address. Defaults to <c>"email"</c>.</summary>
    public string EmailClaimType { get; set; } = "email";

    /// <summary>Claim type used for roles. Defaults to <see cref="ClaimTypes.Role"/>.</summary>
    public string RoleClaimType { get; set; } = ClaimTypes.Role;

    /// <summary>Roles assigned to every authenticated user when no role claims are present.</summary>
    public IReadOnlyList<string> DefaultRolesWhenAuthenticated { get; set; } = [];
}
