using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Abstractions.Features;

/// <summary>
/// Manual registration for feature- and configuration-selected variants, for hosts/tests that wire variants by
/// hand. The <c>[FeatureVariant]</c>/<c>[ConfigurationVariant]</c> generator emits onto the same API, so
/// generated and hand-written registrations stay consistent.
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

    /// <summary>
    /// Registers the configuration-selected variant machinery for <typeparamref name="TService"/>: the binding
    /// (configuration key + default key), the imperative <see cref="IVariantServiceProvider{TService}"/>, and
    /// the transparent unkeyed registration of <typeparamref name="TService"/>. Selection reads
    /// <paramref name="configurationKey"/> through <c>IConfiguration</c> synchronously on each resolution, so —
    /// unlike the feature-selected path — no async proxy or per-scope warm-up is involved and the contract is
    /// injectable anywhere. The caller registers the keyed implementations themselves; <b>keys must be
    /// lower-case</b> (the configured value is lowered before the keyed lookup, making the match
    /// case-insensitive), e.g. <c>services.AddKeyedScoped&lt;IEmailSender, Office365EmailSender&gt;("office365")</c>
    /// and the default under <see cref="VariantServiceKeys.Default"/>. The <c>[ConfigurationVariant]</c>
    /// generator emits onto this API (lower-casing declared values), so generated and hand-written
    /// registrations stay consistent.
    /// </summary>
    public static IServiceCollection AddElarionConfigurationVariantService<TService>(
        this IServiceCollection services,
        string configurationKey,
        string? defaultKey = VariantServiceKeys.Default,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) where TService : class {
        services.TryAddSingleton(new ConfigurationVariantBinding<TService>
            { Key = configurationKey, DefaultKey = defaultKey });
        services.TryAdd(new ServiceDescriptor(
            typeof(IVariantServiceProvider<TService>), typeof(ConfigurationVariantServiceProvider<TService>),
            lifetime));
        // Transparent unkeyed registration: the configuration read is synchronous, so ordinary construction
        // resolves the active implementation directly (no warmed per-scope cache).
        services.TryAdd(new ServiceDescriptor(
            typeof(TService), static sp => ConfigurationVariantServiceProvider<TService>.Resolve(sp), lifetime));

        return services;
    }

    /// <summary>
    /// Seeds the runtime <see cref="IVariantCatalog"/> from the generated compile-time registry — call from the
    /// host with <c>ElarionVariants.All</c>, the one assembly whose registry aggregates every referenced
    /// assembly's switches. Symbols stay host-side; application modules inject <see cref="IVariantCatalog"/>
    /// and consume the data (enumerate switches, validate a requested value) without referencing the declaring
    /// assemblies.
    /// </summary>
    public static IServiceCollection AddElarionVariantCatalog(
        this IServiceCollection services,
        IEnumerable<VariantDescriptor> descriptors) {
        services.TryAddSingleton<IVariantCatalog>(new DefaultVariantCatalog(descriptors));

        return services;
    }
}
