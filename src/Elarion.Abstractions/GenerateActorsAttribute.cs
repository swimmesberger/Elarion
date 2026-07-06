namespace Elarion.Abstractions;

/// <summary>
/// Enables source-generated actor facade and registration emission for the annotated assembly
/// (classes marked with <c>[Actor]</c> from <c>Elarion.Actors</c>). Use
/// <see cref="UseElarionAttribute"/> to enable this together with the other assembly-level
/// framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateActorsAttribute : Attribute;
