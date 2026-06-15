namespace Elarion.Abstractions;

/// <summary>
/// Enables source-generated resilience policy registration for the annotated assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateResiliencePoliciesAttribute : Attribute;
