namespace Elarion.Abstractions.Features;

/// <summary>
/// A typed handle for a declared flag name. The generated <c>ElarionFeatureFlags</c> registry exposes one per
/// declared flag, so imperative code writes <c>flags.IsEnabledAsync(ElarionFeatureFlags.NewExport)</c> and a
/// misspelled or removed flag is a compile error instead of a runtime "unknown flag". It converts implicitly to
/// <see cref="string"/>, so it passes to every <see cref="IFeatureFlagService"/> overload.
/// </summary>
/// <param name="Name">The declared flag name.</param>
public readonly record struct FeatureFlagKey(string Name) {
    /// <summary>The flag name.</summary>
    public static implicit operator string(FeatureFlagKey key) {
        return key.Name;
    }

    /// <inheritdoc />
    public override string ToString() {
        return Name;
    }
}
