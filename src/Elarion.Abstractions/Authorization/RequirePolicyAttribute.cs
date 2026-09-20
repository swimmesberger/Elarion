namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the named authorization policy to pass. The policy is a transport-neutral
/// <see cref="IAuthorizationPolicy"/> resolved from DI by the name declared on its
/// <see cref="AuthorizationPolicyAttribute"/> (or on its <c>AddElarionAuthorizationPolicy</c> registration) and
/// evaluated against the current principal and the handler request — it is <b>not</b> an ASP.NET Core
/// policy, so it works identically under JSON-RPC, MCP, and HTTP.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequirePolicyAttribute(string policy) : Attribute {
    /// <summary>
    /// The policy name, matched against the name declared on the policy's
    /// <see cref="AuthorizationPolicyAttribute"/> or its registration call.
    /// </summary>
    public string Policy { get; } = policy;
}
