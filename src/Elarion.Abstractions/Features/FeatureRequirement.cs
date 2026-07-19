namespace Elarion.Abstractions.Features;

/// <summary>
/// Controls how a <see cref="FeatureGateAttribute"/> combines its listed features. Mirrors the
/// <c>RequirementType</c> of Microsoft's MVC <c>[FeatureGate]</c> so the attribute reads the same way.
/// </summary>
public enum FeatureRequirement {
    /// <summary>Every listed feature must be enabled (logical AND). The default.</summary>
    All,

    /// <summary>At least one listed feature must be enabled (logical OR).</summary>
    Any
}
