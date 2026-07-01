namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// Marks a command handler as idempotent: a retried or duplicated request carrying the same idempotency key
/// executes the operation at most once and replays the first result. Declarative, transport-neutral, and
/// provider-agnostic — the same shape as <c>[Cacheable]</c>/<c>[FeatureGate]</c>.
/// </summary>
/// <remarks>
/// The handler generator attaches the idempotency decorator when this attribute is present. The decorator owns
/// the unit-of-work transaction: it writes the key row atomically with the handler's business writes, lets a
/// database unique constraint reject a duplicate, and replays the stored result. Applies only to
/// <see cref="ICommand"/> handlers whose response can represent failure
/// (<see cref="IResultFailureFactory{TSelf}"/>).
/// </remarks>
/// <example>
/// <code>
/// [Idempotent]
/// public sealed class CreatePaymentHandler : IHandler&lt;CreatePaymentCommand, Result&lt;PaymentResponse&gt;&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class IdempotentAttribute : Attribute {
    /// <summary>How long a completed key is retained and replayable, in hours. Default 24.</summary>
    public int RetentionHours { get; init; } = 24;

    /// <summary>
    /// Whether a request without an idempotency key is rejected with a 400. Default <see langword="true"/>
    /// (an idempotent endpoint always expects a key). Set <see langword="false"/> to run without idempotency
    /// when no key is supplied.
    /// </summary>
    public bool KeyRequired { get; init; } = true;

    /// <summary>Whether the key is scoped per user or globally. Default <see cref="IdempotencyScope.CurrentUser"/>.</summary>
    public IdempotencyScope Scope { get; init; } = IdempotencyScope.CurrentUser;

    /// <summary>
    /// Whether a request fingerprint is stored so reusing the key with a <em>different</em> request body is
    /// rejected (422). Default <see langword="true"/>.
    /// </summary>
    public bool Fingerprint { get; init; } = true;

    /// <summary>Behavior for a concurrent in-flight duplicate. Default <see cref="IdempotencyConflictBehavior.Conflict"/> (409).</summary>
    public IdempotencyConflictBehavior ConflictBehavior { get; init; } = IdempotencyConflictBehavior.Conflict;

    /// <summary>Whether definitive failures are stored and replayed. Default <see cref="IdempotencyFailureStorage.None"/>.</summary>
    public IdempotencyFailureStorage StoreFailures { get; init; } = IdempotencyFailureStorage.None;
}
