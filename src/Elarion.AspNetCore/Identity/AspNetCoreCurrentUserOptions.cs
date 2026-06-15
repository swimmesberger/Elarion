using System.Security.Claims;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Configures how the ASP.NET integration maps claims to <see cref="Framework.Identity.ICurrentUser"/>.
/// </summary>
public sealed class AspNetCoreCurrentUserOptions {
    /// <summary>Claim type used as the stable user identifier.</summary>
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>Claim type used as the user email address.</summary>
    public string EmailClaimType { get; set; } = "email";

    /// <summary>Claim type used for roles.</summary>
    public string RoleClaimType { get; set; } = ClaimTypes.Role;

    /// <summary>Roles assigned to every authenticated user when no role claims are present.</summary>
    public IReadOnlyList<string> DefaultRolesWhenAuthenticated { get; set; } = [];
}

