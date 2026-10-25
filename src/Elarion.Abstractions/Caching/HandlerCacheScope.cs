namespace Elarion.Abstractions.Caching;

/// <summary>
/// Defines the logical scope for handler cache keys and tags.
/// </summary>
/// <remarks>
/// Both scopes sit inside the tenant when ambient tenant scoping is registered: the default cache puts the
/// tenant in scope into every key and tag, so no entry is ever shared between tenants, whichever scope is chosen.
/// </remarks>
public enum HandlerCacheScope {
    /// <summary>
    /// Cache entries and invalidation tags are isolated by the current authenticated user.
    /// </summary>
    /// <remarks>
    /// Requires an authenticated <see cref="Elarion.Abstractions.Identity.ICurrentUser"/> with a user id. The user
    /// id is hashed before being placed into physical cache keys.
    /// </remarks>
    CurrentUser = 0,

    /// <summary>
    /// Cache entries and invalidation tags are shared across all users (all users of the current tenant, under
    /// ambient tenant scoping).
    /// </summary>
    /// <remarks>
    /// Use only for data that is not user-specific and does not depend on authorization context.
    /// </remarks>
    Global = 1
}
