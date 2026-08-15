using System.Globalization;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Logging;

namespace Elarion.Authorization;

/// <summary>
/// The default, transport-neutral <see cref="IAuthorizer"/>. Evaluates every requirement against
/// <see cref="ICurrentUser"/> (claims and roles), the registered <see cref="IGlobalAuthorizationRule"/>
/// instances, and the registered <see cref="IAuthorizationPolicy"/> instances — no <c>HttpContext</c>, no
/// ASP.NET <c>IAuthorizationService</c> — so the same handler authorization works identically under JSON-RPC,
/// MCP, and HTTP.
/// </summary>
/// <remarks>
/// The concrete type is registered alongside the <see cref="IAuthorizer"/> service, so a host can wrap it:
/// register a decorating <see cref="IAuthorizer"/> that injects <see cref="ClaimsAuthorizer"/> as its inner
/// authorizer. Both registrations use <c>TryAdd</c>, so a host registration that runs <b>before</b>
/// <c>AddElarionAuthorization()</c> wins and no <c>RemoveAll</c> is needed.
/// </remarks>
/// <param name="globalRules">
/// Cross-cutting rules evaluated in registration order after the authenticated gate and before the declared
/// requirements. Optional: a host that registers none gets the previous behavior exactly.
/// </param>
public sealed class ClaimsAuthorizer(
    ICurrentUser user,
    IEnumerable<NamedAuthorizationPolicy> policies,
    IResourceAuthorizer resourceAuthorizer,
    AuthorizationOptions options,
    ILogger<ClaimsAuthorizer> logger,
    IEnumerable<IGlobalAuthorizationRule>? globalRules = null
) : IAuthorizer {
    /// <inheritdoc />
    public async ValueTask<AppError?> AuthorizeAsync(
        AuthorizationRequirements requirements, object? resource, CancellationToken ct) {
        // [AllowAnonymous] opts the operation out of authorization entirely, global rules included: a rule must
        // not resurrect a gate the handler deliberately declined.
        if (requirements.AllowAnonymous) return null;

        // Unauthenticated callers fail with 401 before any rule/permission/role/claim/policy check.
        if (requirements.HasAny && !user.IsAuthenticated) return AppError.Unauthorized(options.UnauthorizedMessage);

        if (globalRules is not null) {
            // DI always supplies a (usually empty) enumerable, and this runs for every authorized invocation:
            // build the context on the first rule so a host with no rules allocates nothing.
            AuthorizationContext? context = null;
            foreach (var rule in globalRules) {
                context ??= new AuthorizationContext(user, resource);
                var error = await rule.EvaluateAsync(context, ct).ConfigureAwait(false);
                if (error is null) continue;

                // The rule owns the outcome kind and message (it may deliberately answer NotFound to avoid
                // disclosing a resource), so the error is surfaced verbatim rather than reshaped to Forbidden.
                logger.LogDebug(
                    "Authorization denied by global rule '{Rule}' with {ErrorKind}.",
                    rule.GetType().Name,
                    error.Kind);
                return error;
            }
        }

        foreach (var permission in requirements.Permissions)
            if (!user.HasClaim(options.PermissionClaimType, permission))
                return Forbidden("permission", permission);

        foreach (var role in requirements.Roles)
            if (!user.IsInRole(role))
                return Forbidden("role", role);

        foreach (var claim in requirements.Claims)
            if (!SatisfiesClaim(claim))
                return Forbidden("claim", claim.ClaimType);

        foreach (var policyName in requirements.Policies) {
            var policy = FindPolicy(policyName);
            if (policy is null) {
                // Fail closed: an unregistered policy name denies rather than silently passing.
                logger.LogWarning(
                    "No authorization policy named '{Policy}' is registered; denying the request.", policyName);
                return Forbidden("policy", policyName);
            }

            if (!await policy.EvaluateAsync(new AuthorizationContext(user, resource), ct).ConfigureAwait(false))
                return Forbidden("policy", policyName);
        }

        foreach (var resourceRequirement in requirements.Resources) {
            var context = new ResourceAuthorizationContext(
                user,
                resourceRequirement.ResourceType,
                resourceRequirement.ResourceTypeName,
                resourceRequirement.Operation,
                resourceRequirement.ResourceId);
            if (!await resourceAuthorizer.AuthorizeResourceAsync(context, ct).ConfigureAwait(false))
                return Forbidden("resource", resourceRequirement.ResourceTypeName);
        }

        return null;
    }

    private IAuthorizationPolicy? FindPolicy(string name) {
        foreach (var named in policies)
            if (string.Equals(named.Name, name, StringComparison.Ordinal))
                return named.Policy;

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
        return AppError.Forbidden(string.Format(CultureInfo.InvariantCulture, options.ForbiddenMessageFormat,
            requirement));
    }
}
