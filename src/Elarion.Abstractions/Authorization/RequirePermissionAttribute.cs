namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the current principal to hold the named permission. This is sugar for
/// <c>[RequireClaim(permissionClaimType, permission)]</c> over the configured permission claim type
/// (see <see cref="AuthorizationOptions.PermissionClaimType"/>, default <c>"permission"</c>); it saves
/// repeating the claim type for the common permission case.
/// </summary>
/// <remarks>
/// The optional <paramref name="kind"/> classifies the permission for the generated
/// <c>ElarionPermissions.ByKind</c> grouping only; it does not change the authorization decision.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission, PermissionKind kind = PermissionKind.Unspecified)
    : Attribute {
    /// <summary>The required permission value.</summary>
    public string Permission { get; } = permission;

    /// <summary>The optional classification driving <c>ElarionPermissions.ByKind</c> (default <see cref="PermissionKind.Unspecified"/>).</summary>
    public PermissionKind Kind { get; } = kind;
}
