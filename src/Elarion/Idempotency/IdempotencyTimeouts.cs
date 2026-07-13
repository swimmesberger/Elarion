namespace Elarion.Idempotency;

/// <summary>
/// Transport-scoped idempotency timing invariants shared by the decorator and the store tiers, so every
/// backend bounds a <see cref="Elarion.Abstractions.Idempotency.IdempotencyConflictBehavior.WaitThenReplay"/>
/// wait the same way.
/// </summary>
internal static class IdempotencyTimeouts {
    /// <summary>
    /// The ceiling on how long a <c>WaitThenReplay</c> duplicate blocks on the in-flight winner before
    /// degrading to the "in progress" conflict outcome. On the EF/PostgreSQL tier the decorator enforces it
    /// via <c>SET LOCAL lock_timeout</c> on the claim row; the in-memory store enforces it directly on its
    /// completion-signal wait.
    /// </summary>
    internal static readonly TimeSpan WaitThenReplayCeiling = TimeSpan.FromSeconds(30);
}
