using Elarion.Abstractions.Authorization;

namespace Elarion.AspNetCore.Identity;

/// <summary>Options for <see cref="ElarionIdentityServiceCollectionExtensions.AddElarionIdentity{TUser, TRole, TKey, TDbContext}"/>.</summary>
public sealed class ElarionIdentityOptions {
    /// <summary>Whether to register Identity's default token providers (for password reset, 2FA, etc.). Defaults to <c>true</c>.</summary>
    public bool AddDefaultTokenProviders { get; set; } = true;

    /// <summary>Authorization options forwarded to <c>AddElarionAuthorization</c> (e.g. the permission claim type).</summary>
    public AuthorizationOptions Authorization { get; set; } = new();

    /// <summary>Optional override of the ASP.NET current-user claim mapping (defaults to the Identity claim types).</summary>
    public Action<AspNetCoreCurrentUserOptions>? ConfigureCurrentUser { get; set; }
}
