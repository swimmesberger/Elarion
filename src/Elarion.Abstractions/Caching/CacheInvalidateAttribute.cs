namespace Elarion.Abstractions.Caching;

/// <summary>
/// Marks a mutating handler as invalidating cached entries associated with the specified logical tags.
/// </summary>
/// <remarks>
/// Invalidation runs only after the inner handler returns a successful <c>Result&lt;T&gt;</c>.
/// Failed validation or business results do not evict cached reads.
/// </remarks>
/// <param name="tags">Logical cache tags to invalidate after a successful handler result.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CacheInvalidateAttribute(params string[] tags) : Attribute {
    /// <summary>Logical cache tags to invalidate after a successful handler result.</summary>
    public string[] Tags { get; } = tags;

    /// <summary>
    /// Controls whether invalidation applies to the current user's entries or globally shared entries.
    /// </summary>
    public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;
}
