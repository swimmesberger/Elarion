namespace Elarion.Abstractions.Features;

/// <summary>
/// The owner of every <b>backend-evaluated</b> feature flag (<see cref="BackendFeatureFlagAttribute"/>): the one
/// seam that hands a flag name and an explicit <see cref="FeatureEvaluationContext"/> to an external flag system
/// — Microsoft.FeatureManagement or any OpenFeature provider (LaunchDarkly, ConfigCat, flagd, …) — and returns its
/// answer. The provider packages (<c>Elarion.FeatureFlags.FeatureManagement</c>/<c>.OpenFeature</c>) register it.
/// </summary>
/// <remarks>
/// A backend is only ever asked about flags a module declared as backend-evaluated; it never sees a code-defined
/// flag, and it is never consulted "as a fallback" for an undeclared name. Registering more than one evaluator is
/// not supported — a flag has exactly one owner, and the backend is the owner of all flags declared with
/// <see cref="BackendFeatureFlagAttribute"/>.
/// </remarks>
public interface IBackendFeatureFlagEvaluator {
    /// <summary>Returns whether <paramref name="flag"/> is enabled for <paramref name="context"/>.</summary>
    ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct);

    /// <summary>
    /// Returns the variant the backend allocated to <paramref name="context"/> for <paramref name="flag"/>, or
    /// <see langword="null"/> when the flag is off, has no variant, or the backend could not resolve one.
    /// </summary>
    ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct);
}
