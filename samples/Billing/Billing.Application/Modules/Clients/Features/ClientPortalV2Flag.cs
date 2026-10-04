using Elarion.Abstractions.Features;

namespace Billing.Application.Modules.Clients.Features;

/// <summary>
/// A code-defined, client-exposed flag: this class is the flag's only owner. It evaluates against the explicit
/// <see cref="FeatureEvaluationContext"/>, so the same answer comes back from the session snapshot, a gate, or an
/// admin previewing another user's experience.
/// </summary>
[FeatureFlag("client-portal-v2", Description = "The redesigned client portal, for administrators.", ExposeToClient = true)]
public sealed class ClientPortalV2Flag : IFeatureFlagResolver {
    public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
        return ValueTask.FromResult(context.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase));
    }
}
