using Elarion.Abstractions;
using Elarion.Abstractions.Caching;

namespace Elarion.Pipeline;

/// <summary>
/// Wraps a handler with cache lookup and population behavior using a generated cache policy.
/// </summary>
public sealed class CacheDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IHandlerCache cache,
    IHandlerCachePolicy<TRequest> policy
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    // Note 5: This is the decorator pattern: callers see the same handler interface, but behavior is added around the inner handler.
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
        // Note 6: Passing the cancellation token through both cache and factory paths prevents cached handlers from ignoring request cancellation.
        await cache.GetOrCreateAsync(policy, request, token => inner.HandleAsync(request, token), ct);
}
