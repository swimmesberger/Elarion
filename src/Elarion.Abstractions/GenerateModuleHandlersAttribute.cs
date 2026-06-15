namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of per-module handler registration methods.
/// Place on assembly: <c>[assembly: GenerateModuleHandlers]</c>. Use
/// <see cref="UseElarionAttribute"/> to enable this together with the other
/// assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateModuleHandlersAttribute : Attribute;
