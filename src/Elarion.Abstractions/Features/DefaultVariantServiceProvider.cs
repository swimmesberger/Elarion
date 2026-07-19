using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Features;

/// <summary>
/// Default <see cref="IVariantServiceProvider{TService}"/>: resolves the allocated variant name via
/// <see cref="IFeatureVariantService"/>, then resolves the implementation keyed by that name from DI, falling back
/// to the default-keyed implementation. Provider-neutral and AOT-safe (keyed resolution over a constructed generic
/// is reflection-free; the implementations are statically rooted by the generated keyed registrations).
/// </summary>
public sealed class DefaultVariantServiceProvider<TService>(
    IFeatureVariantService variants,
    VariantServiceBinding<TService> binding,
    IServiceProvider services
) : IVariantServiceProvider<TService> where TService : class {
    /// <inheritdoc />
    public async ValueTask<TService> GetAsync(CancellationToken ct = default) {
        return await GetOrDefaultAsync(ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException(
                   $"No variant implementation could be resolved for feature '{binding.Feature}' (service "
                   + $"'{typeof(TService)}') and no default implementation is registered.");
    }

    /// <inheritdoc />
    public async ValueTask<TService?> GetOrDefaultAsync(CancellationToken ct = default) {
        var variant = await variants.GetVariantAsync(binding.Feature, ct).ConfigureAwait(false);

        var selected = variant is not null ? services.GetKeyedService<TService>(variant) : null;
        if (selected is not null) return selected;

        return binding.DefaultKey is { } defaultKey ? services.GetKeyedService<TService>(defaultKey) : null;
    }
}
