using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;

namespace Elarion.Tests.Authorization;

/// <summary>A configurable <see cref="ICurrentUser"/> test double — no ASP.NET, proving transport neutrality.</summary>
internal sealed class FakeCurrentUser : ICurrentUser {
    public string UserId { get; init; } = "user-1";
    public string? Email { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public bool IsAuthenticated { get; init; }
    public IReadOnlyList<(string Type, string Value)> Claims { get; init; } = [];

    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);

    public bool HasClaim(string type, string value) =>
        Claims.Any(claim => claim.Type == type && claim.Value == value);

    public IEnumerable<string> GetClaimValues(string type) =>
        Claims.Where(claim => claim.Type == type).Select(claim => claim.Value);
}

/// <summary>A pass-through inner handler that records whether it ran and returns a fixed response.</summary>
internal sealed class StubInnerHandler<TRequest, TResponse>(TResponse response) : IHandler<TRequest, TResponse> {
    public bool WasInvoked { get; private set; }

    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        WasInvoked = true;
        return ValueTask.FromResult(response);
    }
}

/// <summary>A named policy that asserts the request flows through as the resource and checks an "age" claim.</summary>
[AuthorizationPolicy("AtLeast21")]
internal sealed class AtLeast21Policy : IAuthorizationPolicy {
    public object? LastResource { get; private set; }

    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
        LastResource = context.Resource;
        var satisfied = context.User.GetClaimValues("age")
            .Any(value => int.TryParse(value, out var age) && age >= 21);
        return ValueTask.FromResult(satisfied);
    }
}

// Handlers whose class-level attributes the decorator reads through HandlerMetadata.
internal sealed record GuardedCommand(int Id);

[RequirePermission("tenants.write")]
internal sealed class RequirePermissionHandler;

[RequireRole("Admin")]
internal sealed class RequireRoleHandler;

[RequirePermission("a")]
[RequirePermission("b")]
internal sealed class RequireBothPermissionsHandler;

[RequireClaim("scope", "read", "write")]
internal sealed class RequireClaimOrHandler;

[RequirePolicy("AtLeast21")]
[RequireRole("Admin")]
internal sealed class PolicyAndRoleHandler;

[RequirePermission("tenants.write")]
[AllowAnonymous]
internal sealed class AnonymousWinsHandler;

internal sealed class NoAttributesHandler;
