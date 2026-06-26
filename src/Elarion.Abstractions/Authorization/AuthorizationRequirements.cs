namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The authorization requirements resolved for a handler: the requirements its attributes declare plus
/// whether a default-authorization policy applies. Built by
/// <see cref="AuthorizationDecorator{TRequest, TResponse}"/> and evaluated by an <see cref="IAuthorizer"/>.
/// </summary>
/// <param name="AllowAnonymous">When <see langword="true"/>, authorization is skipped entirely.</param>
/// <param name="RequireAuthenticated">When <see langword="true"/>, the principal must be authenticated.</param>
/// <param name="Permissions">Required permission values (each mapped to the configured permission claim type).</param>
/// <param name="Roles">Required roles.</param>
/// <param name="Claims">Required claims.</param>
/// <param name="Policies">Required named policy names.</param>
public readonly record struct AuthorizationRequirements(
    bool AllowAnonymous,
    bool RequireAuthenticated,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Roles,
    IReadOnlyList<RequireClaimAttribute> Claims,
    IReadOnlyList<string> Policies) {
    /// <summary>Whether any requirement beyond "anonymous" is present.</summary>
    public bool HasAny =>
        RequireAuthenticated || Permissions.Count > 0 || Roles.Count > 0 || Claims.Count > 0 || Policies.Count > 0;
}
