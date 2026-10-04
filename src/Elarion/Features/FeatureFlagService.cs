using System.Collections.Concurrent;
using Elarion.Abstractions.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Features;

/// <summary>
/// The default <see cref="IFeatureFlagService"/>: a catalog lookup followed by a dispatch to the flag's single
/// owner. There is no precedence and no fallback — a code-defined flag is answered by its
/// <see cref="IFeatureFlagResolver"/>, a backend-evaluated flag by the host's
/// <see cref="IBackendFeatureFlagEvaluator"/>, and a name the catalog does not contain is disabled and logged once.
/// </summary>
internal sealed class FeatureFlagService(
    FeatureFlagCatalog catalog,
    IServiceProvider services,
    ILogger<FeatureFlagService> logger) : IFeatureFlagService {
    // Logged once per name per process: an unknown flag evaluated on every request must not flood the log.
    private static readonly ConcurrentDictionary<string, byte> ReportedUnknown = new(StringComparer.Ordinal);

    public FeatureEvaluationContext CreateContext() {
        return FeatureEvaluationContext.FromScope(services);
    }

    public ValueTask<bool> IsEnabledAsync(string flag, CancellationToken ct = default) {
        return IsEnabledAsync(flag, CreateContext(), ct);
    }

    public ValueTask<string?> GetVariantAsync(string flag, CancellationToken ct = default) {
        return GetVariantAsync(flag, CreateContext(), ct);
    }

    public async ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryFind(flag, out var registration)) return false;

        if (registration.ResolverFactory is { } factory)
            return await factory(context).IsEnabledAsync(context, ct).ConfigureAwait(false);

        return await Backend(context, flag).IsEnabledAsync(flag, context, ct).ConfigureAwait(false);
    }

    public async ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryFind(flag, out var registration)) return null;

        if (registration.ResolverFactory is { } factory)
            return await factory(context).GetVariantAsync(context, ct).ConfigureAwait(false);

        return await Backend(context, flag).GetVariantAsync(flag, context, ct).ConfigureAwait(false);
    }

    private bool TryFind(string flag, out FeatureFlagRegistration registration) {
        if (catalog.TryGetRegistration(flag, out registration)) return true;

        if (ReportedUnknown.TryAdd(flag, 0))
            logger.LogWarning(
                "Feature flag '{Flag}' is not declared by any enabled module and evaluates as disabled. Declare it with "
                + "[FeatureFlag]/[BackendFeatureFlag] in a module, or enable the module that declares it.",
                flag);

        return false;
    }

    private static IBackendFeatureFlagEvaluator Backend(FeatureEvaluationContext context, string flag) {
        return context.Services.GetService<IBackendFeatureFlagEvaluator>()
               ?? throw new InvalidOperationException(
                   $"Feature flag '{flag}' is backend-evaluated but no IBackendFeatureFlagEvaluator is registered. "
                   + "Register a backend (AddElarionFeatureManagement or AddElarionOpenFeature).");
    }
}
