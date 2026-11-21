namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Options for the authorization building blocks. Registered as a plain singleton — the abstractions
/// package deliberately has no <c>Microsoft.Extensions.Options</c> dependency.
/// </summary>
public sealed class AuthorizationOptions {
    /// <summary>
    /// The default of <see cref="PermissionClaimType"/> (<c>"permission"</c>) — the claim type the declarative pieces
    /// that cannot read these options (<c>[ResourceFilter].PermissionClaimType</c>,
    /// <see cref="QueryAuthorizers.WhenPermission{TEntity}(string, IQueryAuthorizer{TEntity}, string)"/>) fall back to.
    /// </summary>
    public const string DefaultPermissionClaimType = "permission";

    /// <summary>The claim type <see cref="RequirePermissionAttribute"/> maps to. Defaults to <c>"permission"</c>.</summary>
    public string PermissionClaimType { get; set; } = DefaultPermissionClaimType;

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

    /// <summary>
    /// Message of the not-found answer to a <c>[RequireResource(DenyAs = ResourceDenial.NotFound)]</c> denial. Keep it
    /// identical to the handler's own answer for a missing resource, or the two become distinguishable.
    /// </summary>
    public string ResourceNotFoundMessage { get; set; } = "The requested resource was not found.";
}
