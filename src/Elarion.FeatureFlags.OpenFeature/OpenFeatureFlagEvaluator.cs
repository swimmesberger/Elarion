using Elarion.Abstractions.Features;
using OpenFeature;

namespace Elarion.FeatureFlags.OpenFeature;

/// <summary>
/// The <see cref="IBackendFeatureFlagEvaluator"/> backed by the OpenFeature <see cref="IFeatureClient"/>: the
/// single owner of every flag declared with <c>[BackendFeatureFlag]</c>. It evaluates against an OpenFeature
/// context built from the explicit <see cref="FeatureEvaluationContext"/> (see <see cref="ElarionEvaluationContext"/>),
/// so targeting follows whoever the caller passed, not whoever the ambient scope happens to hold.
/// </summary>
/// <remarks>
/// Enablement is a boolean evaluation that defaults to <c>false</c> (a fail-safe closed gate). The variant is read
/// from the flag-resolution details (<c>FlagEvaluationDetails.Variant</c>, OpenFeature spec §1.4.6), which any
/// conforming provider populates.
/// </remarks>
public sealed class OpenFeatureFlagEvaluator(IFeatureClient client) : IBackendFeatureFlagEvaluator {
    /// <inheritdoc />
    public async ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct) {
        return await client
            .GetBooleanValueAsync(flag, false, ElarionEvaluationContext.Create(context), cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct) {
        // The default value is a fail-safe sentinel; we read .Variant (the allocated variant name), not .Value.
        var details = await client
            .GetStringDetailsAsync(flag, string.Empty, ElarionEvaluationContext.Create(context), cancellationToken: ct)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(details.Variant) ? null : details.Variant;
    }
}
