using Elarion.Abstractions.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.OpenFeature;

/// <summary>
/// Registration helpers for the OpenFeature-backed Elarion feature-flag provider.
/// </summary>
public static class OpenFeatureServiceCollectionExtensions {
    /// <summary>
    /// Registers the OpenFeature-backed <see cref="IFeatureFlagService"/> over the OpenFeature
    /// <c>IFeatureClient</c>. The host is responsible for configuring OpenFeature and a provider — typically
    /// <c>services.AddOpenFeature(b =&gt; b.AddProvider(...))</c> — so this can compose with any backend
    /// (LaunchDarkly, ConfigCat, flagd, Microsoft.FeatureManagement via <c>AddElarionFeatureManagement</c>, ...).
    /// </summary>
    public static IServiceCollection AddElarionOpenFeature(this IServiceCollection services) {
        services.TryAddScoped<IFeatureFlagService, OpenFeatureFeatureFlagService>();

        return services;
    }
}
