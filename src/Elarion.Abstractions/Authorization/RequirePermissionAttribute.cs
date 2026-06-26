namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the current principal to hold the named permission. This is sugar for
/// <c>[RequireClaim(permissionClaimType, permission)]</c> over the configured permission claim type
/// (see <see cref="AuthorizationOptions.PermissionClaimType"/>, default <c>"permission"</c>); it saves
/// repeating the claim type for the common permission case.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute {
    /// <summary>The required permission value.</summary>
    public string Permission { get; } = permission;
}
