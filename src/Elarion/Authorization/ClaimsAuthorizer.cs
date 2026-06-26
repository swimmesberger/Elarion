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
                return Forbidden(permission);
            }
        }

        foreach (var role in requirements.Roles) {
            if (!user.IsInRole(role)) {
                return Forbidden(role);
            }
        }

        foreach (var claim in requirements.Claims) {
            if (!SatisfiesClaim(claim)) {
                return Forbidden(claim.ClaimType);
            }
        }

        foreach (var policyName in requirements.Policies) {
            var policy = FindPolicy(policyName);
            if (policy is null) {
                // Fail closed: an unregistered policy name denies rather than silently passing.
                logger.LogWarning(
                    "No authorization policy named '{Policy}' is registered; denying the request.", policyName);
                return Forbidden(policyName);
            }

            if (!await policy.EvaluateAsync(new AuthorizationContext(user, resource), ct).ConfigureAwait(false)) {
                return Forbidden(policyName);
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

    private AppError Forbidden(string requirement) =>
        AppError.Forbidden(string.Format(CultureInfo.InvariantCulture, options.ForbiddenMessageFormat, requirement));
}
