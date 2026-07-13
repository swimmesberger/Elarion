using Elarion.Abstractions;
using Elarion.Abstractions.Caching;

namespace Elarion.Pipeline;

/// <summary>
/// Invalidates cache tags after an inner handler completes with a successful result.
/// </summary>
public sealed class CacheInvalidationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IHandlerCache cache,
    IHandlerCacheInvalidationPolicy policy
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        // The write operation runs first so failed commands do not evict still-valid cached reads.
        var response = await inner.HandleAsync(request, ct);

        // Cache invalidation is tied to Result<T> success, keeping validation/business failures side-effect free.
        if (response is IResultLike { IsSuccess: true }) {
            await cache.InvalidateAsync(policy, ct);
        }

        return response;
    }
}
