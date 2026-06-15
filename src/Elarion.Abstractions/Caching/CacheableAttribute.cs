namespace Elarion.Abstractions.Caching;

/// <summary>
/// Marks a handler as cacheable and provides compile-time metadata used by the handler registration generator.
/// </summary>
/// <remarks>
/// Only successful <c>Result&lt;T&gt;</c> responses are cached. Failed results are returned to
/// the caller but deliberately not stored.
/// </remarks>
/// <param name="tags">Logical cache tags used for grouping and invalidation.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CacheableAttribute(params string[] tags) : Attribute {
    /// <summary>Logical cache tags used for grouping and invalidation.</summary>
    public string[] Tags { get; } = tags;

    /// <summary>
    /// Entry lifetime in seconds for both distributed and local cache layers.
    /// </summary>
    public int DurationSeconds { get; init; } = 60;

    /// <summary>
    /// Controls whether generated keys and tags are scoped to the current user or shared globally.
    /// </summary>
    public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;

    /// <summary>
    /// Optional request property names to include in the generated key. When empty, all public request properties are used.
    /// </summary>
    /// <remarks>
    /// Use this when only some request properties affect the response. Property names are read
    /// by the source generator and invalid names are reported at build time.
    /// </remarks>
    public string[] KeyProperties { get; init; } = [];
}
