using System.Text.Json;

namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The generated, per-handler idempotency policy: the compile-time <c>[Idempotent]</c> options plus
/// AOT-safe serialization of the handler's <c>Result&lt;T&gt;</c> outcome for storage and replay. Mirrors
/// <see cref="Elarion.Abstractions.Caching.IHandlerCachePayloadPolicy{TRequest, TResponse}"/>.
/// </summary>
/// <typeparam name="TRequest">The handler request type.</typeparam>
/// <typeparam name="TResponse">The handler response type (a <c>Result&lt;T&gt;</c>).</typeparam>
public interface IIdempotencyPayloadPolicy<in TRequest, TResponse> {
    /// <summary>The key scope.</summary>
    IdempotencyScope Scope { get; }

    /// <summary>Whether a missing key is rejected (400) rather than passed through.</summary>
    bool KeyRequired { get; }

    /// <summary>Whether a request fingerprint is computed and enforced.</summary>
    bool Fingerprint { get; }

    /// <summary>Behavior for a concurrent in-flight duplicate.</summary>
    IdempotencyConflictBehavior ConflictBehavior { get; }

    /// <summary>Whether definitive failures are stored and replayed.</summary>
    IdempotencyFailureStorage StoreFailures { get; }

    /// <summary>How long a completed record is retained/replayable.</summary>
    TimeSpan Retention { get; }

    /// <summary>
    /// The fixed owner discriminator for <see cref="IdempotencyScope.Consumer"/>-scoped keys: the consuming
    /// handler's identity, baked in by the generator (ADR-0022 inbox). <see langword="null"/> for the
    /// caller-derived scopes (<see cref="IdempotencyScope.CurrentUser"/>/<see cref="IdempotencyScope.Global"/>),
    /// whose owner comes from the current user or is empty.
    /// </summary>
    string? Owner => null;

    /// <summary>Serializes the handler outcome (success value, or — when storing failures — the error) for storage.</summary>
    string Serialize(TResponse response, JsonSerializerOptions options);

    /// <summary>Reconstructs the handler outcome from a stored payload for replay.</summary>
    TResponse Deserialize(string payload, JsonSerializerOptions options);
}
