namespace Elarion.Abstractions.Features;

/// <summary>
/// Resolves the <typeparamref name="TService"/> implementation allocated to the current user by a feature flag's
/// variant. This is the <i>imperative</i> escape hatch — transparent constructor injection of
/// <typeparamref name="TService"/> is the primary DX (the async-resolving handler proxy warms it). Use this
/// provider directly when a service needs the variant outside a proxied handler's construction, or to re-resolve
/// per call.
/// </summary>
public interface IVariantServiceProvider<TService> where TService : class {
    /// <summary>
    /// Resolves the variant implementation for the current user, falling back to the registered default. Throws
    /// when neither a variant nor a default implementation can be resolved.
    /// </summary>
    ValueTask<TService> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetAsync"/> but returns <c>null</c> instead of throwing when neither a variant nor a
    /// default implementation is registered.
    /// </summary>
    ValueTask<TService?> GetOrDefaultAsync(CancellationToken ct = default);
}
