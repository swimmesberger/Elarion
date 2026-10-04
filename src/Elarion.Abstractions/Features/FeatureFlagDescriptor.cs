namespace Elarion.Abstractions.Features;

/// <summary>Who evaluates a declared feature flag. Every flag has exactly one owner.</summary>
public enum FeatureFlagOwner {
    /// <summary>A resolver class in the declaring module (<see cref="FeatureFlagAttribute"/>).</summary>
    Code,

    /// <summary>The host's <see cref="IBackendFeatureFlagEvaluator"/> (<see cref="BackendFeatureFlagAttribute"/>).</summary>
    Backend
}

/// <summary>
/// One declared feature flag as the runtime catalog knows it: the data the generators lifted out of the
/// <see cref="FeatureFlagAttribute"/>/<see cref="BackendFeatureFlagAttribute"/> declaration.
/// </summary>
public sealed record FeatureFlagDescriptor {
    /// <summary>The flag name.</summary>
    public required string Name { get; init; }

    /// <summary>The declaring module (the <c>[AppModule]</c> name).</summary>
    public required string Module { get; init; }

    /// <summary>What the flag controls, or <see langword="null"/> when undocumented.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the session snapshot evaluates and returns the flag to the frontend.</summary>
    public bool ExposeToClient { get; init; }

    /// <summary>The flag's single owner.</summary>
    public required FeatureFlagOwner Owner { get; init; }
}
