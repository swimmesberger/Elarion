namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of per-module authorization-policy registration methods (one per
/// <c>[AuthorizationPolicy]</c> class). Place on the assembly: <c>[assembly: GenerateModuleAuthorizationPolicies]</c>.
/// Use <see cref="UseElarionAttribute"/> to enable this together with the other assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateModuleAuthorizationPoliciesAttribute : Attribute;
