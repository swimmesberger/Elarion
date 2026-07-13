using System.Globalization;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Elarion.Authorization;

/// <summary>
/// The default, transport-neutral <see cref="IAuthorizer"/>. Evaluates every requirement against
/// <see cref="ICurrentUser"/> (claims and roles) and the registered <see cref="IAuthorizationPolicy"/>
/// instances — no <c>HttpContext</c>, no ASP.NET <c>IAuthorizationService</c> — so the same handler
/// authorization works identically under JSON-RPC, MCP, and HTTP.
/// </summary>
public sealed class ClaimsAuthorizer(
    ICurrentUser user,
    IEnumerable<NamedAuthorizationPolicy> policies,
    IResourceAuthorizer resourceAuthorizer,
    AuthorizationOptions options,
    ILogger<ClaimsAuthorizer> logger
) : IAuthorizer {
    /// <inheritdoc />
    public async ValueTask<AppError?> AuthorizeAsync(
        AuthorizationRequirements requirements, object? resource, CancellationToken ct) {
        if (requirements.AllowAnonymous) {
            return null;
        }

        // Unauthenticated callers fail with 401 before any permission/role/claim/policy check.
        if (requirements.HasAny && !user.IsAuthenticated) {
            return AppError.Unauthorized(options.UnauthorizedMessage);
        }

        foreach (var permission in requirements.Permissions) {
            if (!user.HasClaim(options.PermissionClaimType, permission)) {
                return Forbidden("permission", permission);
            }
        }

        foreach (var role in requirements.Roles) {
            if (!user.IsInRole(role)) {
                return Forbidden("role", role);
            }
        }

        foreach (var claim in requirements.Claims) {
            if (!SatisfiesClaim(claim)) {
                return Forbidden("claim", claim.ClaimType);
            }
        }

        foreach (var policyName in requirements.Policies) {
            var policy = FindPolicy(policyName);
            if (policy is null) {
                // Fail closed: an unregistered policy name denies rather than silently passing.
                logger.LogWarning(
                    "No authorization policy named '{Policy}' is registered; denying the request.", policyName);
                return Forbidden("policy", policyName);
            }

            if (!await policy.EvaluateAsync(new AuthorizationContext(user, resource), ct).ConfigureAwait(false)) {
                return Forbidden("policy", policyName);
            }
        }

        foreach (var resourceRequirement in requirements.Resources) {
            var context = new ResourceAuthorizationContext(
                user,
                resourceRequirement.ResourceType,
                resourceRequirement.ResourceTypeName,
                resourceRequirement.Operation,
                resourceRequirement.ResourceId);
            if (!await resourceAuthorizer.AuthorizeResourceAsync(context, ct).ConfigureAwait(false)) {
                return Forbidden("resource", resourceRequirement.ResourceTypeName);
            }
        }

        return null;
    }

    private IAuthorizationPolicy? FindPolicy(string name) {
        foreach (var named in policies) {
            if (string.Equals(named.Name, name, StringComparison.Ordinal)) {
                return named.Policy;
            }
        }

        return null;
    }

    private bool SatisfiesClaim(RequireClaimAttribute claim) {
        var values = user.GetClaimValues(claim.ClaimType);
        return claim.AllowedValues.Count == 0
            ? values.Any()
            : values.Any(value => claim.AllowedValues.Contains(value, StringComparer.Ordinal));
    }

    private AppError Forbidden(string requirementKind, string requirement) {
        // The wire message defaults to a generic "Access denied." so a forbidden caller never learns the
        // permission vocabulary; the unmet requirement stays available to operators through this log.
        logger.LogDebug(
            "Authorization denied: unmet {RequirementKind} requirement '{Requirement}'.",
            requirementKind,
            requirement);
        return AppError.Forbidden(string.Format(CultureInfo.InvariantCulture, options.ForbiddenMessageFormat, requirement));
    }
}
