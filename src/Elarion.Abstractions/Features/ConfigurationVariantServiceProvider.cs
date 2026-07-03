using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Features;

/// <summary>
/// <see cref="IVariantServiceProvider{TService}"/> for configuration-selected variants: reads the bound
/// configuration key, resolves the implementation keyed by the configured value, and falls back to the
/// default-keyed implementation. Selection is synchronous (a configuration lookup), so the async surface
/// completes synchronously and the transparent unkeyed contract registration resolves through the same logic
/// with no proxy and no per-scope warm-up — unlike the feature-selected
/// <see cref="DefaultVariantServiceProvider{TService}"/>.
/// </summary>
public sealed class ConfigurationVariantServiceProvider<TService>(
    IConfiguration configuration,
    ConfigurationVariantBinding<TService> binding,
    IServiceProvider services
) : IVariantServiceProvider<TService> where TService : class {
    /// <inheritdoc />
    public ValueTask<TService> GetAsync(CancellationToken ct = default) =>
        new(ResolveRequired(services, configuration, binding));

    /// <inheritdoc />
    public ValueTask<TService?> GetOrDefaultAsync(CancellationToken ct = default) =>
        new(ResolveOrDefault(services, configuration, binding));

    /// <summary>
    /// Resolves the active implementation for the transparent unkeyed contract registration. Throws when
    /// neither the configured value nor a default implementation can be resolved.
    /// </summary>
    public static TService Resolve(IServiceProvider services) => ResolveRequired(
        services,
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<ConfigurationVariantBinding<TService>>());

    private static TService ResolveRequired(
        IServiceProvider services, IConfiguration configuration, ConfigurationVariantBinding<TService> binding) =>
        ResolveOrDefault(services, configuration, binding)
        ?? throw new InvalidOperationException(
            $"No variant implementation could be resolved for configuration key '{binding.Key}' (service "
            + $"'{typeof(TService)}') and no default implementation is registered.");

    private static TService? ResolveOrDefault(
        IServiceProvider services, IConfiguration configuration, ConfigurationVariantBinding<TService> binding) {
        var value = configuration[binding.Key];
        // Variant keys are registered lower-case; lowering the configured value makes the match
        // case-insensitive (a human-typed "Office365" selects the "office365" variant).
        var selected = string.IsNullOrWhiteSpace(value)
            ? null
            : services.GetKeyedService<TService>(value.ToLowerInvariant());
        if (selected is not null) {
            return selected;
        }

        return binding.DefaultKey is { } defaultKey ? services.GetKeyedService<TService>(defaultKey) : null;
    }
}
