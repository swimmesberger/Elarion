namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Options for the authorization building blocks. Registered as a plain singleton — the abstractions
/// package deliberately has no <c>Microsoft.Extensions.Options</c> dependency.
/// </summary>
public sealed class AuthorizationOptions {
    /// <summary>The claim type <see cref="RequirePermissionAttribute"/> maps to. Defaults to <c>"permission"</c>.</summary>
    public string PermissionClaimType { get; set; } = "permission";

    /// <summary>Format string for the forbidden message; <c>{0}</c> is the failed requirement.</summary>
    public string ForbiddenMessageFormat { get; set; } = "Missing required permission: {0}";

    /// <summary>Message returned when the principal is not authenticated.</summary>
    public string UnauthorizedMessage { get; set; } = "Authentication required.";
}
