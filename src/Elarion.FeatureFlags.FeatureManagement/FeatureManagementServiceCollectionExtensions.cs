using Elarion.FeatureFlags.OpenFeature;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Contrib.Providers.FeatureManagement;

namespace Elarion.FeatureFlags.FeatureManagement;

/// <summary>
/// Registration helper for the Microsoft.FeatureManagement-backed default Elarion feature-flag provider.
/// </summary>
public static class FeatureManagementServiceCollectionExtensions {
    /// <summary>
    /// Registers the Microsoft.FeatureManagement OpenFeature provider as the default backend for Elarion's
    /// OpenFeature-backed <see cref="Elarion.Abstractions.Features.IFeatureFlagService"/>. Flag definitions are read
    /// from <paramref name="configuration"/> (the conventional <c>FeatureManagement</c>/<c>feature_management</c>
    /// section), so <c>[FeatureGate]</c> works out of the box. This is one line of sugar over
    /// <c>AddOpenFeature(...)</c> + <c>AddElarionOpenFeature()</c> — to use a different OpenFeature provider, call
    /// those directly instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration carrying the feature flag definitions.</param>
    public static IServiceCollection AddElarionFeatureManagement(
        this IServiceCollection services,
        IConfiguration configuration) {
        services.AddOpenFeature(builder =>
            builder.AddProvider(_ => new FeatureManagementProvider(configuration)));

        services.AddElarionOpenFeature();

        return services;
    }
}
