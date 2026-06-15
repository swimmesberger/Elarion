namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of per-module service registration methods.
/// Place on assembly: <c>[assembly: GenerateModuleServices]</c>. Use
/// <see cref="UseElarionAttribute"/> to enable this together with the other
/// assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateModuleServicesAttribute : Attribute;
