namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Marks an <see cref="IAuthorizationPolicy"/> implementation as a named policy that is **auto-registered**
/// per module (like <c>[Service]</c>), and declares the policy <see cref="Name"/> referenced by
/// <see cref="RequirePolicyAttribute"/>. Because the name is a compile-time constant here, it is also the
/// metadata a future analyzer can use to validate <c>[RequirePolicy("…")]</c> references.
/// </summary>
/// <example>
/// <code>
/// [AuthorizationPolicy("AtLeast21")]
/// public sealed class AtLeast21Policy(TimeProvider clock) : IAuthorizationPolicy {
///     public ValueTask&lt;bool&gt; EvaluateAsync(AuthorizationContext context, CancellationToken ct) { /* ... */ }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AuthorizationPolicyAttribute(string name) : Attribute {
    /// <summary>The policy name, matched against <see cref="RequirePolicyAttribute.Policy"/>.</summary>
    public string Name { get; } = name;
}
