namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The logical scope an idempotency key is keyed under. Mirrors
/// <see cref="Elarion.Abstractions.Caching.HandlerCacheScope"/>.
/// </summary>
public enum IdempotencyScope {
    /// <summary>
    /// The key is isolated per authenticated user, so the same key value from two different users never
    /// collides and one user can never replay another's stored result. Requires an authenticated caller.
    /// </summary>
    CurrentUser = 0,

    /// <summary>The key is shared across all callers (a single global namespace).</summary>
    Global = 1,
}
