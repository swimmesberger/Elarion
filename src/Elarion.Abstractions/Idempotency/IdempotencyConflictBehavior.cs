namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// How a request that arrives while another request with the same idempotency key is still in flight is
/// handled.
/// </summary>
public enum IdempotencyConflictBehavior {
    /// <summary>
    /// Fail fast with a 409 Conflict ("request already in progress") — the IETF/Stripe industry standard.
    /// The caller retries shortly and then replays the first request's stored result. Requires a backend that
    /// can fast-fail a blocked claim (PostgreSQL <c>lock_timeout</c>); backends that cannot degrade to
    /// <see cref="WaitThenReplay"/>.
    /// </summary>
    Conflict = 0,

    /// <summary>
    /// Block on the key's lock until the first request commits, then replay its result — no 409 for the
    /// caller to handle, at the cost of holding a connection while waiting.
    /// </summary>
    WaitThenReplay = 1
}
