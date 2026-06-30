using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using OpenFeature;

namespace Elarion.FeatureFlags.OpenFeature;

/// <summary>
/// Default <see cref="IFeatureVariantService"/> backed by the OpenFeature <see cref="IFeatureClient"/>. It reads
/// the allocated variant from the flag-resolution details (<c>FlagEvaluationDetails.Variant</c>, OpenFeature spec
/// §1.4.6), which any conforming provider populates — so variant selection is provider-neutral.
/// </summary>
public sealed class OpenFeatureFeatureVariantService(IFeatureClient client, ICurrentUser currentUser)
    : IFeatureVariantService {
    /// <inheritdoc />
    public async ValueTask<string?> GetVariantAsync(string feature, CancellationToken ct = default) {
        var context = ElarionEvaluationContext.Create(currentUser);

        // The default value is a fail-safe sentinel; we read .Variant (the allocated variant name), not .Value.
        var details = await client
            .GetStringDetailsAsync(feature, defaultValue: string.Empty, context, cancellationToken: ct)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(details.Variant) ? null : details.Variant;
    }
}
