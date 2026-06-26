namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the named authorization policy to pass. The policy is a transport-neutral
/// <see cref="IAuthorizationPolicy"/> resolved from DI by <see cref="IAuthorizationPolicy.Name"/> and
/// evaluated against the current principal and the handler request — it is <b>not</b> an ASP.NET Core
/// policy, so it works identically under JSON-RPC, MCP, and HTTP.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequirePolicyAttribute(string policy) : Attribute {
    /// <summary>The policy name, matched against <see cref="IAuthorizationPolicy.Name"/>.</summary>
    public string Policy { get; } = policy;
}
