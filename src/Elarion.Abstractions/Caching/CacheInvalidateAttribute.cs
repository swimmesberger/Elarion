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
    /// <remarks>
    /// Defaults to <see cref="HandlerCacheScope.Global"/>: invalidation is a mutation reacting to a state change,
    /// and the caller performing the mutation is usually <b>not</b> the user whose cached read must be evicted (an
    /// admin editing another user's record, for example). A <see cref="HandlerCacheScope.CurrentUser"/> default
    /// would invalidate only the mutator's own tag and leave every affected user permanently stale, so the safe
    /// default is over-invalidation (Global). A <see cref="HandlerCacheScope.Global"/>-scoped invalidation evicts
    /// the globally shared entries <b>and</b> every user's <see cref="HandlerCacheScope.CurrentUser"/>-scoped
    /// entries for the listed tags (user-scoped entries also carry the global tag namespace precisely so this
    /// pairing works). Set <see cref="HandlerCacheScope.CurrentUser"/> explicitly only for a genuinely per-user
    /// cache the mutating caller also owns — it clears the invoking user's entries and nothing else.
    /// </remarks>
    public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.Global;
}
