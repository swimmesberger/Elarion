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
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        // Passing the cancellation token through both cache and factory paths prevents cached handlers from ignoring request cancellation.
        return await cache.GetOrCreateAsync(policy, request, token => inner.HandleAsync(request, token), ct);
    }
}
