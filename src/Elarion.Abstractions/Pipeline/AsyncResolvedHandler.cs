namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// A handler proxy that defers building the real decorator pipeline to the first (asynchronous)
/// <see cref="HandleAsync"/> call, so a dependency that can only be resolved asynchronously — such as a
/// feature-flag <i>variant</i> service selected for the current user — can be <c>await</c>ed during construction.
/// </summary>
/// <remarks>
/// <para>
/// DI constructor injection is synchronous, but every transport already invokes handlers asynchronously. The
/// source generator emits a shared synchronous pipeline-build method and, for a handler that depends on a variant
/// service, an asynchronous one that first resolves the variant; it registers this proxy (instead of the plain
/// synchronous factory) only for those handlers, so handlers that need nothing async pay nothing. The
/// <paramref name="build"/> delegate is the generated async builder; it is invoked once per scope and cached.
/// </para>
/// <para>
/// This type is provider-agnostic — it knows nothing about feature flags. The real pipeline is built once per
/// scope; it must not be cached across scopes (the chain captures scoped state).
/// </para>
/// </remarks>
public sealed class AsyncResolvedHandler<TRequest, TResponse>(
    IServiceProvider scope,
    Func<IServiceProvider, CancellationToken, ValueTask<IHandler<TRequest, TResponse>>> build
) : IHandler<TRequest, TResponse> {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IHandler<TRequest, TResponse>? _inner;

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var inner = await GetInnerAsync(ct).ConfigureAwait(false);

        return await inner.HandleAsync(request, ct).ConfigureAwait(false);
    }

    // Single-flight build. Two concurrent first calls on the same scoped instance — the framework-endorsed
    // IHandlerSender fan-out (Task.WhenAll on one command type in one scope) — must build the inner pipeline
    // exactly once, not race a double build.
    private async ValueTask<IHandler<TRequest, TResponse>> GetInnerAsync(CancellationToken ct) {
        var inner = Volatile.Read(ref _inner);
        if (inner is not null) return inner;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            inner = _inner;
            if (inner is null) {
                inner = await build(scope, ct).ConfigureAwait(false);
                Volatile.Write(ref _inner, inner);
            }
        }
        finally {
            _gate.Release();
        }

        return inner;
    }
}
