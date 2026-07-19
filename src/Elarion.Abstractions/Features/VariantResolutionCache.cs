using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Features;

/// <summary>
/// Per-scope holder for variant services already resolved (the allocated implementation for the current user).
/// The generated handler proxy <see cref="Pipeline.AsyncResolvedHandler{TRequest,TResponse}"/> calls
/// <see cref="WarmAsync{TService}"/> once before building the handler pipeline; the transparent unkeyed
/// registration of the contract then reads the warmed instance through <see cref="Get{TService}"/>, so the
/// consuming handler injects the contract synchronously and unaware of variants.
/// </summary>
/// <remarks>
/// Scoped: the resolved variant is specific to the current user/request, so a value must never leak across scopes.
/// <see cref="Get{TService}"/> throws when the contract was not warmed — which happens when the contract is
/// injected outside a proxied handler's construction (e.g. into a singleton or a non-handler service reached
/// without the handler declaring the dependency). In that case inject
/// <see cref="IVariantServiceProvider{TService}"/> and resolve explicitly.
/// </remarks>
public sealed class VariantResolutionCache {
    // A scope can be used concurrently: two first invocations of the same command type in one scope (the
    // IHandlerSender fan-out via Task.WhenAll) both warm/read this cache, so a plain Dictionary would tear.
    private readonly Dictionary<Type, object> _resolved = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Resolves the variant implementation of <typeparamref name="TService"/> for the current user (once) and
    /// stores it. Idempotent within the scope, and safe under concurrent first calls.
    /// </summary>
    public async ValueTask WarmAsync<TService>(IServiceProvider services, CancellationToken ct)
        where TService : class {
        if (ContainsResolved(typeof(TService)))
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (ContainsResolved(typeof(TService)))
                return;

            var provider = services.GetRequiredService<IVariantServiceProvider<TService>>();
            var instance = await provider.GetAsync(ct).ConfigureAwait(false);
            lock (_resolved) {
                _resolved[typeof(TService)] = instance;
            }
        }
        finally {
            _gate.Release();
        }
    }

    private bool ContainsResolved(Type service) {
        lock (_resolved) {
            return _resolved.ContainsKey(service);
        }
    }

    /// <summary>
    /// Returns the warmed variant implementation of <typeparamref name="TService"/>, or throws when it was not
    /// warmed in this scope.
    /// </summary>
    public TService Get<TService>() where TService : class {
        lock (_resolved) {
            if (_resolved.TryGetValue(typeof(TService), out var instance))
                return (TService)instance;
        }

        throw new InvalidOperationException(
            $"No variant of '{typeof(TService)}' was resolved in this scope. A variant contract is resolved "
            + "transparently only when injected directly into a handler; inject "
            + $"IVariantServiceProvider<{typeof(TService).Name}> to resolve it elsewhere.");
    }
}
