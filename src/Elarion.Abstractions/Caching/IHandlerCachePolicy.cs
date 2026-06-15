using System.Text.Json;

namespace Elarion.Abstractions.Caching;

/// <summary>
/// Describes cache behavior for one generated handler/request pair.
/// </summary>
/// <typeparam name="TRequest">The handler request type.</typeparam>
public interface IHandlerCachePolicy<in TRequest> {
    /// <summary>
    /// Stable prefix identifying the handler and generated key version.
    /// </summary>
    /// <remarks>
    /// Changing this value effectively invalidates old entries because new requests use a new
    /// physical key namespace.
    /// </remarks>
    string KeyPrefix { get; }

    /// <summary>Duration for cache entries created by this policy.</summary>
    TimeSpan Expiration { get; }

    /// <summary>Logical scope for keys and tags created by this policy.</summary>
    HandlerCacheScope Scope { get; }

    /// <summary>
    /// Logical tags associated with entries created by this policy.
    /// </summary>
    /// <remarks>
    /// Tags must match the tags used by <see cref="IHandlerCacheInvalidationPolicy"/> for
    /// mutating handlers that should invalidate these entries.
    /// </remarks>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Creates the request-specific key suffix.
    /// </summary>
    /// <remarks>
    /// Generated policies typically build this from selected request properties using
    /// <see cref="HandlerCacheKey"/>.
    /// </remarks>
    string CreateKey(TRequest request);
}

/// <summary>
/// Serializes and deserializes cache payloads for one generated handler/request/response pair.
/// </summary>
public interface IHandlerCachePayloadPolicy<in TRequest, TResponse> : IHandlerCachePolicy<TRequest> {
    /// <summary>
    /// Serializes the successful handler response into a cache payload.
    /// </summary>
    string Serialize(TResponse response, JsonSerializerOptions options);

    /// <summary>
    /// Deserializes a cache payload back into the handler response type.
    /// </summary>
    TResponse Deserialize(string payload, JsonSerializerOptions options);
}

/// <summary>
/// Describes the logical cache tags invalidated by one generated handler policy.
/// </summary>
public interface IHandlerCacheInvalidationPolicy {
    /// <summary>Logical scope for tags invalidated by this policy.</summary>
    HandlerCacheScope Scope { get; }

    /// <summary>Logical tags invalidated by this policy.</summary>
    IReadOnlyList<string> Tags { get; }
}
