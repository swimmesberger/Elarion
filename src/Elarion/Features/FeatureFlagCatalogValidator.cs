using Elarion.Abstractions.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elarion.Features;

/// <summary>
/// Fails host startup when the catalog contains backend-evaluated flags but no
/// <see cref="IBackendFeatureFlagEvaluator"/> is registered — the one ownership gap the compiler cannot see,
/// because the backend is chosen by the host's composition root. Failing here beats every gated handler quietly
/// answering "not found" at run time.
/// </summary>
internal sealed class FeatureFlagCatalogValidator(
    IFeatureFlagCatalog catalog,
    IServiceProvider services) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        var backendFlags = catalog.All.Where(static flag => flag.Owner == FeatureFlagOwner.Backend).ToArray();
        if (backendFlags.Length > 0 && !IsRegistered(services))
            throw new InvalidOperationException(
                $"The module catalog declares {backendFlags.Length} backend-evaluated feature flag(s) "
                + $"({string.Join(", ", backendFlags.Take(5).Select(static f => $"'{f.Name}'"))}"
                + $"{(backendFlags.Length > 5 ? ", ..." : string.Empty)}) but no IBackendFeatureFlagEvaluator is registered. "
                + "Register a backend (AddElarionFeatureManagement or AddElarionOpenFeature).");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }

    private static bool IsRegistered(IServiceProvider services) {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetService(typeof(IBackendFeatureFlagEvaluator)) is not null;
    }
}
