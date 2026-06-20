namespace Elarion.Abstractions;

/// <summary>
/// Enables source-generated event consumer descriptor registration for the annotated assembly.
/// Use <see cref="UseElarionAttribute"/> to enable this together with the other
/// assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateEventConsumersAttribute : Attribute;
