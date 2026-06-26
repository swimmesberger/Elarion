using Elarion.Abstractions.Identity;

namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A named, transport-neutral authorization policy referenced by <see cref="RequirePolicyAttribute"/>.
/// Resolved from DI by <see cref="Name"/> and evaluated against the current principal and the handler
/// request — the in-process analog of an ASP.NET Core policy, without the HTTP coupling.
/// </summary>
/// <example>
/// <code>
/// public sealed class AtLeast21Policy(TimeProvider clock) : IAuthorizationPolicy {
///     public string Name => "AtLeast21";
///     public ValueTask&lt;bool&gt; EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
///         var birthDate = context.User.GetClaimValues("birthdate").FirstOrDefault();
///         // ... compute age against clock.GetUtcNow() ...
///         return ValueTask.FromResult(/* age >= 21 */);
///     }
/// }
/// </code>
/// </example>
public interface IAuthorizationPolicy {
    /// <summary>The policy name, matched against <see cref="RequirePolicyAttribute.Policy"/>.</summary>
    string Name { get; }

    /// <summary>Returns whether the policy is satisfied for the given context.</summary>
    ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct);
}

/// <summary>The context supplied to an <see cref="IAuthorizationPolicy"/>.</summary>
public sealed class AuthorizationContext(ICurrentUser user, object? resource) {
    /// <summary>The current principal (claims and roles).</summary>
    public ICurrentUser User { get; } = user;

    /// <summary>The handler request being authorized (the policy resource), if any.</summary>
    public object? Resource { get; } = resource;
}
