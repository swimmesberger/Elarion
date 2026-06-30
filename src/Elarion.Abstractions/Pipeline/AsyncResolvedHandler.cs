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
    private IHandler<TRequest, TResponse>? _inner;

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        _inner ??= await build(scope, ct).ConfigureAwait(false);

        return await _inner.HandleAsync(request, ct).ConfigureAwait(false);
    }
}
