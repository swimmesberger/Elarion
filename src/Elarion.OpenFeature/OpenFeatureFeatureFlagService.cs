using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using OpenFeature;

namespace Elarion.OpenFeature;

/// <summary>
/// Default <see cref="IFeatureFlagService"/> implementation backed by the OpenFeature <see cref="IFeatureClient"/>.
/// It evaluates the flag as a boolean (defaulting to <c>false</c> — a fail-safe closed gate) against an
/// <see cref="ElarionEvaluationContext"/> derived from the current user, so <c>[FeatureGate]</c> works against any
/// configured OpenFeature provider.
/// </summary>
public sealed class OpenFeatureFeatureFlagService(IFeatureClient client, ICurrentUser currentUser)
    : IFeatureFlagService {
    /// <inheritdoc />
    public async ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) {
        var context = ElarionEvaluationContext.Create(currentUser);

        return await client.GetBooleanValueAsync(feature, defaultValue: false, context, cancellationToken: ct)
            .ConfigureAwait(false);
    }
}
