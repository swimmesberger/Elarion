namespace Elarion.Abstractions.Caching;

/// <summary>
/// Provides handler-oriented cache operations used by generated decorator wiring.
/// </summary>
/// <remarks>
/// Implementations decide how logical policies map to physical cache keys and tags. The
/// default implementation uses HybridCache and avoids storing failed <c>Result&lt;T&gt;</c> values.
/// </remarks>
public interface IHandlerCache {
    /// <summary>
    /// Gets a cached response or invokes <paramref name="factory"/> and stores a successful response.
    /// </summary>
    /// <remarks>
    /// Implementations should treat unsuccessful result-like responses as non-cacheable so
    /// validation or domain failures do not become sticky.
    /// </remarks>
    ValueTask<TResponse> GetOrCreateAsync<TRequest, TResponse>(
        IHandlerCachePolicy<TRequest> policy,
        TRequest request,
        Func<CancellationToken, ValueTask<TResponse>> factory,
        CancellationToken ct);

    /// <summary>
    /// Invalidates entries matching the supplied handler cache invalidation policy.
    /// </summary>
    /// <remarks>
    /// Invalidation is tag-based; callers do not need to know the concrete keys produced by
    /// read handlers.
    /// </remarks>
    ValueTask InvalidateAsync(IHandlerCacheInvalidationPolicy policy, CancellationToken ct);
}
