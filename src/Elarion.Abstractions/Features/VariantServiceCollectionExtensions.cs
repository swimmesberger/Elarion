using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Abstractions.Features;

/// <summary>
/// Manual registration for feature variants, for hosts/tests that wire variants by hand. The
/// <c>[FeatureVariant&lt;T&gt;]</c> generator emits onto the same API, so generated and hand-written registrations
/// stay consistent.
/// </summary>
public static class VariantServiceCollectionExtensions {
    /// <summary>
    /// Registers the variant-selection machinery for <typeparamref name="TService"/>: the per-scope resolution
    /// cache, the binding (feature + default key), the imperative <see cref="IVariantServiceProvider{TService}"/>,
    /// and the transparent unkeyed registration of <typeparamref name="TService"/> (which reads the warmed cache).
    /// The caller registers the keyed implementations themselves, e.g.
    /// <c>services.AddKeyedScoped&lt;IForecastAlgorithm, NeuralForecast&gt;("neural")</c> and the default under
    /// <see cref="VariantServiceKeys.Default"/>.
    /// </summary>
    public static IServiceCollection AddElarionVariantService<TService>(
        this IServiceCollection services,
        string feature,
        string? defaultKey = VariantServiceKeys.Default,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) where TService : class {
        services.TryAddScoped<VariantResolutionCache>();
        services.TryAddSingleton(new VariantServiceBinding<TService> { Feature = feature, DefaultKey = defaultKey });
        services.TryAdd(new ServiceDescriptor(
            typeof(IVariantServiceProvider<TService>), typeof(DefaultVariantServiceProvider<TService>), lifetime));
        // Transparent unkeyed registration: ordinary construction reads the value the proxy warmed.
        services.TryAdd(new ServiceDescriptor(
            typeof(TService), sp => sp.GetRequiredService<VariantResolutionCache>().Get<TService>(), lifetime));

        return services;
    }
}
