using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Features;

/// <summary>
/// Registration of declared feature flags. The module source generator emits one call per declaration into each
/// module's default services, so a flag exists exactly when its module is enabled; calling these by hand is for
/// tests and hosts that compose flags without generation.
/// </summary>
public static class FeatureFlagServiceCollectionExtensions {
    /// <summary>
    /// Registers a code-defined flag owned by <typeparamref name="TResolver"/> (scoped, resolved from the
    /// evaluation context's services), plus the catalog and <see cref="IFeatureFlagService"/>.
    /// </summary>
    /// <typeparam name="TResolver">The flag's resolver — its single owner.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The flag name.</param>
    /// <param name="module">The declaring module's name.</param>
    /// <param name="description">What the flag controls.</param>
    /// <param name="exposeToClient">Whether the session snapshot returns the flag to the frontend.</param>
    public static IServiceCollection AddElarionFeatureFlag<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TResolver>(
        this IServiceCollection services,
        string name,
        string module,
        string? description = null,
        bool exposeToClient = false) where TResolver : class, IFeatureFlagResolver {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<TResolver>();
        return AddRegistration(services, new FeatureFlagRegistration(
            Describe(name, module, description, exposeToClient, FeatureFlagOwner.Code),
            static context => context.Services.GetRequiredService<TResolver>()));
    }

    /// <summary>
    /// Registers a backend-evaluated flag, owned by the host's <see cref="IBackendFeatureFlagEvaluator"/>, plus
    /// the catalog and <see cref="IFeatureFlagService"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The flag name.</param>
    /// <param name="module">The declaring module's name.</param>
    /// <param name="description">What the flag controls.</param>
    /// <param name="exposeToClient">Whether the session snapshot returns the flag to the frontend.</param>
    public static IServiceCollection AddElarionBackendFeatureFlag(
        this IServiceCollection services,
        string name,
        string module,
        string? description = null,
        bool exposeToClient = false) {
        ArgumentNullException.ThrowIfNull(services);

        return AddRegistration(services, new FeatureFlagRegistration(
            Describe(name, module, description, exposeToClient, FeatureFlagOwner.Backend), null));
    }

    private static FeatureFlagDescriptor Describe(
        string name, string module, string? description, bool exposeToClient, FeatureFlagOwner owner) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return new FeatureFlagDescriptor {
            Name = name,
            Module = module,
            Description = description,
            ExposeToClient = exposeToClient,
            Owner = owner
        };
    }

    private static IServiceCollection AddRegistration(IServiceCollection services, FeatureFlagRegistration registration) {
        services.AddSingleton(registration);
        services.TryAddSingleton<FeatureFlagCatalog>(static sp =>
            new FeatureFlagCatalog(sp.GetServices<FeatureFlagRegistration>()));
        services.TryAddSingleton<IFeatureFlagCatalog>(static sp => sp.GetRequiredService<FeatureFlagCatalog>());
        services.TryAddScoped<IFeatureFlagService, FeatureFlagService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FeatureFlagCatalogValidator>());

        return services;
    }
}
