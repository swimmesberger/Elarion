namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Options for the authorization building blocks. Registered as a plain singleton — the abstractions
/// package deliberately has no <c>Microsoft.Extensions.Options</c> dependency.
/// </summary>
public sealed class AuthorizationOptions {
    /// <summary>The claim type <see cref="RequirePermissionAttribute"/> maps to. Defaults to <c>"permission"</c>.</summary>
    public string PermissionClaimType { get; set; } = "permission";

    /// <summary>
    /// Format string for the forbidden message; <c>{0}</c> is the failed requirement. The default is a
    /// generic <c>"Access denied."</c> that deliberately omits <c>{0}</c>, so a forbidden caller never
    /// learns which permission/role/policy it lacks (consistent with the opaque feature-gate 404). The
    /// unmet requirement is still logged at debug level by the default authorizer; set a format
    /// containing <c>{0}</c> (e.g. <c>"Missing required permission: {0}"</c>) to echo it on the wire.
    /// </summary>
    public string ForbiddenMessageFormat { get; set; } = "Access denied.";

    /// <summary>Message returned when the principal is not authenticated.</summary>
    public string UnauthorizedMessage { get; set; } = "Authentication required.";
}
