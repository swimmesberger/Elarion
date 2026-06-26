using Elarion.Abstractions.Authorization;

namespace Elarion.Authorization;

/// <summary>
/// Binds an <see cref="IAuthorizationPolicy"/> to the name that <see cref="RequirePolicyAttribute"/> references.
/// The name lives here (from <c>[AuthorizationPolicy("…")]</c> or the registration call), not on the policy
/// implementation, so <see cref="ClaimsAuthorizer"/> resolves a named policy without the implementation
/// carrying its own name.
/// </summary>
public sealed class NamedAuthorizationPolicy(string name, IAuthorizationPolicy policy) {
    /// <summary>The policy name.</summary>
    public string Name { get; } = name;

    /// <summary>The policy implementation.</summary>
    public IAuthorizationPolicy Policy { get; } = policy;
}
