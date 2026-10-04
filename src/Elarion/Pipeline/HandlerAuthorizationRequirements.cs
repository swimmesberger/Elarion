using System.Reflection;
using System.Runtime.CompilerServices;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Pipeline;

namespace Elarion.Pipeline;

/// <summary>
/// Reads the authorization requirements a handler declares (<c>[RequirePermission]</c>, <c>[RequireRole]</c>,
/// <c>[RequireClaim]</c>, <c>[RequirePolicy]</c>, <c>[AllowAnonymous]</c>) off its concrete type, once per type, and
/// merges the default-authentication policy in per call. Shared by <see cref="AuthorizationDecorator{TRequest,TResponse}"/>
/// and <see cref="AuthorizationGate"/> so both always evaluate the same requirements.
/// </summary>
internal static class HandlerAuthorizationRequirements {
    // Parsed-from-attributes requirements are cached per concrete handler type; the cheap default-policy
    // flag is merged in per call so attribute reflection runs once per handler type.
    private static readonly ConditionalWeakTable<Type, RequirementsBox> Cache = new();

    public static AuthorizationRequirements Resolve(HandlerMetadata metadata, bool requireAuthenticatedByDefault) {
        var parsed = Cache.GetValue(metadata.HandlerType, static type => new RequirementsBox(Parse(type))).Value;
        return requireAuthenticatedByDefault && !parsed.AllowAnonymous && !parsed.RequireAuthenticated
            ? parsed with { RequireAuthenticated = true }
            : parsed;
    }

    private static AuthorizationRequirements Parse(Type handlerType) {
        var allowAnonymous = handlerType.GetCustomAttribute<AllowAnonymousAttribute>(true) is not null;
        var permissions = handlerType.GetCustomAttributes<RequirePermissionAttribute>(true)
            .Select(static attribute => attribute.Permission).ToArray();
        var roles = handlerType.GetCustomAttributes<RequireRoleAttribute>(true)
            .Select(static attribute => attribute.Role).ToArray();
        var claims = handlerType.GetCustomAttributes<RequireClaimAttribute>(true).ToArray();
        var policies = handlerType.GetCustomAttributes<RequirePolicyAttribute>(true)
            .Select(static attribute => attribute.Policy).ToArray();

        return new AuthorizationRequirements(
            allowAnonymous,
            false,
            permissions,
            roles,
            claims,
            policies,
            []);
    }

    // ConditionalWeakTable requires a reference-type value, so the requirements struct is boxed once per type.
    private sealed class RequirementsBox(AuthorizationRequirements value) {
        public AuthorizationRequirements Value { get; } = value;
    }
}
