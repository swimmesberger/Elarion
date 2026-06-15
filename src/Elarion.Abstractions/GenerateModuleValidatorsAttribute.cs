namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of per-module validator registration methods.
/// Place on assembly: <c>[assembly: GenerateModuleValidators]</c>. Use
/// <see cref="UseElarionAttribute"/> to enable this together with the other
/// assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateModuleValidatorsAttribute : Attribute;
