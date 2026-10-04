using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Modules;

namespace Elarion.Session;

/// <summary>
/// The framework-shipped client-capability bootstrap handler. It composes existing seams only — the deployment
/// <see cref="ClientCapabilityManifest"/>, the current <see cref="ICurrentUser"/>, and (when present) the
/// <see cref="IFeatureFlagCatalog"/>/<see cref="IFeatureFlagService"/> — into a single <see cref="SessionResponse"/>
/// the frontend reflects. See <c>ADR-0030</c> and <c>ADR-0079</c>.
/// </summary>
/// <remarks>
/// The flag services are optional: a host that does not use feature flags still gets the module map and
/// the user's grants. Only the catalog flags declared with <c>ExposeToClient</c> are evaluated — and the catalog
/// holds only <b>enabled</b> modules' flags — so nothing internal leaks. Every flag is evaluated through the same
/// <see cref="IFeatureFlagService"/> and one <see cref="FeatureEvaluationContext"/> as the handler gates, so the
/// snapshot cannot disagree with the gate. A flag is reported as a variant only when its owner allocates one;
/// otherwise it appears as a boolean flag, so a pure UI flag is first-class.
/// Registered <see cref="IClientSnapshotContributor"/> instances add named application sections on top of that fixed
/// shape.
/// </remarks>
public sealed class SessionHandler(
    ICurrentUser currentUser,
    ClientCapabilityManifest manifest,
    AuthorizationOptions? authorizationOptions = null,
    IFeatureFlagCatalog? catalog = null,
    IFeatureFlagService? featureFlags = null,
    IEnumerable<IClientSnapshotContributor>? contributors = null)
    : IHandler<SessionRequest, Result<SessionResponse>> {
    /// <inheritdoc/>
    public async ValueTask<Result<SessionResponse>> HandleAsync(SessionRequest request, CancellationToken ct) {
        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var variants = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in manifest.Modules)
            modules[module.Name] = module.Enabled;

        if (catalog is not null && featureFlags is not null) {
            var context = featureFlags.CreateContext();
            foreach (var flag in catalog.All) {
                if (!flag.ExposeToClient) continue;

                flags[flag.Name] = await featureFlags.IsEnabledAsync(flag.Name, context, ct).ConfigureAwait(false);

                var variant = await featureFlags.GetVariantAsync(flag.Name, context, ct).ConfigureAwait(false);
                if (variant is not null) variants[flag.Name] = variant;
            }
        }

        var permissionClaimType = authorizationOptions?.PermissionClaimType ?? "permission";
        var user = new SessionUser {
            // An anonymous caller has no id, and the contract is non-nullable: ICurrentUser.UserId is documented to be
            // consulted only after IsAuthenticated (the shipped ClaimsPrincipalCurrentUser throws for an unauthenticated
            // principal). Session bootstrap is anonymous-friendly, so project the empty id SessionUser.Id promises.
            Id = currentUser.IsAuthenticated ? currentUser.UserId : string.Empty,
            Email = currentUser.Email,
            IsAuthenticated = currentUser.IsAuthenticated,
            Roles = currentUser.Roles,
            Permissions = [.. currentUser.GetClaimValues(permissionClaimType)]
        };

        return new SessionResponse {
            User = user,
            Modules = modules,
            Flags = flags,
            Variants = variants,
            Sections = await BuildSectionsAsync(ct).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Collects the contributed sections, or <see langword="null"/> when nothing was contributed — the dictionary
    /// is only allocated once a contributor actually produces a payload, so the no-contributor host keeps the
    /// original wire shape (<c>sections</c> omitted) and pays nothing.
    /// </summary>
    private async ValueTask<IReadOnlyDictionary<string, object?>?> BuildSectionsAsync(CancellationToken ct) {
        if (contributors is null) return null;

        HashSet<string>? names = null;
        Dictionary<string, object?>? sections = null;
        foreach (var contributor in contributors) {
            // Names are checked before the payload is consulted, so a duplicate registration fails on every
            // request rather than only when both contributors happen to produce a payload: a wire key that
            // silently drops one contributor's data (or hands the frontend the wrong payload under a name it
            // trusts) is a wiring bug, and it should fail the same way every time.
            names ??= new HashSet<string>(StringComparer.Ordinal);
            if (!names.Add(contributor.SectionName)) {
                throw new InvalidOperationException(
                    $"Two client snapshot contributors declare the section '{contributor.SectionName}'. " +
                    "Section names are wire keys and must be unique across registered IClientSnapshotContributor " +
                    "instances.");
            }

            var payload = await contributor.GetSectionAsync(ct).ConfigureAwait(false);
            if (payload is null) continue;

            sections ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            sections[contributor.SectionName] = payload;
        }

        return sections;
    }
}
