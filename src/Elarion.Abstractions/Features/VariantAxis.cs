namespace Elarion.Abstractions.Features;

/// <summary>The selection axis of a variant contract — what decides which implementation resolves.</summary>
public enum VariantAxis {
    /// <summary>Selected per user by a feature flag's allocated variant (<c>[FeatureVariant]</c>).</summary>
    Feature,

    /// <summary>Selected process-globally by a configuration value (<c>[ConfigurationVariant]</c>).</summary>
    Configuration
}
