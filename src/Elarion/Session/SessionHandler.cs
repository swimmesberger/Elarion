using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;

namespace Elarion.Session;

/// <summary>
/// The framework-shipped client-capability bootstrap handler. It composes existing seams only — the deployment
/// <see cref="ClientCapabilityManifest"/>, the current <see cref="ICurrentUser"/>, and (when present) the
/// <see cref="IFeatureFlagService"/>/<see cref="IFeatureVariantService"/> — into a single <see cref="SessionResponse"/>
/// the frontend reflects. See <c>ADR-0020</c>.
/// </summary>
/// <remarks>
/// The flag and variant services are optional: a host that does not use feature flags still gets the module map and
/// the user's grants. Only the names a module declared via <c>[ClientFeatures]</c> (and only for <b>enabled</b>
/// modules) are evaluated, so nothing internal leaks. A name is reported as a variant only when the variant accessor
/// resolves one; otherwise it appears as a boolean flag, so a pure UI flag is first-class.
/// </remarks>
public sealed class SessionHandler(
    ICurrentUser currentUser,
    ClientCapabilityManifest manifest,
    AuthorizationOptions? authorizationOptions = null,
    IFeatureFlagService? featureFlags = null,
    IFeatureVariantService? featureVariants = null)
    : IHandler<SessionRequest, Result<SessionResponse>> {
    /// <inheritdoc/>
    public async ValueTask<Result<SessionResponse>> HandleAsync(SessionRequest request, CancellationToken ct) {
        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var variants = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in manifest.Modules) {
            modules[module.Name] = module.Enabled;
            if (!module.Enabled) {
                continue;
            }

            foreach (var feature in module.Features) {
                if (featureFlags is not null && !flags.ContainsKey(feature)) {
                    flags[feature] = await featureFlags.IsEnabledAsync(feature, ct).ConfigureAwait(false);
                }

                if (featureVariants is not null && !variants.ContainsKey(feature)) {
                    var variant = await featureVariants.GetVariantAsync(feature, ct).ConfigureAwait(false);
                    if (variant is not null) {
                        variants[feature] = variant;
                    }
                }
            }
        }

        var permissionClaimType = authorizationOptions?.PermissionClaimType ?? "permission";
        var user = new SessionUser {
            Id = currentUser.UserId,
            Email = currentUser.Email,
            IsAuthenticated = currentUser.IsAuthenticated,
            Roles = currentUser.Roles,
            Permissions = [.. currentUser.GetClaimValues(permissionClaimType)],
        };

        return new SessionResponse {
            User = user,
            Modules = modules,
            Flags = flags,
            Variants = variants,
        };
    }
}
