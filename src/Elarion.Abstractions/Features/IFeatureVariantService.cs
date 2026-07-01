namespace Elarion.Abstractions.Features;

/// <summary>
/// Transport-neutral accessor for the allocated <i>variant</i> of a multivariate feature flag — the dedicated
/// accessor <see cref="IFeatureFlagService"/> deliberately does not provide (see ADR-0016). It is a sibling of
/// the boolean seam, so a backend may implement boolean enablement only, or boolean plus variants, independently.
/// </summary>
/// <remarks>
/// AOT-clean and provider-agnostic (string in / string out). The default implementation
/// (<c>Elarion.FeatureFlags.OpenFeature</c>) reads the variant from OpenFeature's flag-resolution details, which
/// every conforming provider populates. Targeting is ambient (resolved from <c>ICurrentUser</c>), like
/// <see cref="IFeatureFlagService"/>.
/// </remarks>
public interface IFeatureVariantService {
    /// <summary>
    /// Returns the variant name allocated to the current ambient targeting context for <paramref name="feature"/>,
    /// or <c>null</c> when the flag is off, has no variant, or the provider could not resolve one.
    /// </summary>
    ValueTask<string?> GetVariantAsync(string feature, CancellationToken ct = default);
}
