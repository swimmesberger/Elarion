namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// Whether an <c>[Idempotent]</c> handler stores and replays failed outcomes, in addition to successes.
/// </summary>
public enum IdempotencyFailureStorage {
    /// <summary>
    /// Success-only (default): a failed result rolls back the transaction, discarding the key, so the same
    /// key stays retryable. This is the modern consensus (Stripe v2, AWS Powertools) and needs no savepoint.
    /// </summary>
    None = 0,

    /// <summary>
    /// Also store and replay <em>definitive</em> failures (<see cref="ErrorKind.Validation"/>,
    /// <see cref="ErrorKind.BusinessRule"/>, <see cref="ErrorKind.NotFound"/>,
    /// <see cref="ErrorKind.Forbidden"/>), so a retry returns the identical failure instead of re-running.
    /// Transient failures (<see cref="ErrorKind.Internal"/>, concurrency-loss) stay retryable. Implemented via
    /// a savepoint that discards the handler's business writes while keeping the key row — a single
    /// transaction, no atomicity loss.
    /// </summary>
    Definitive = 1
}
