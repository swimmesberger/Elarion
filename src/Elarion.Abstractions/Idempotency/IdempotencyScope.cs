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

    /// <summary>
    /// The key is isolated per event consumer: the owner discriminator is the consuming handler's identity
    /// (supplied by the generated policy via <see cref="IIdempotencyPayloadPolicy{TRequest, TResponse}.Owner"/>),
    /// and the key is the delivered message id. This is the inbox pattern (ADR-0022) — one event fans out to many
    /// consumers, so each consumer's claim must be namespaced from its siblings' or the first claim would make
    /// every other consumer skip its own distinct work.
    /// </summary>
    Consumer = 2,
}
