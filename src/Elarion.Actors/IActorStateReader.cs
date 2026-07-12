using System.Text.Json;
using Elarion.Abstractions.Serialization;

namespace Elarion.Actors;

/// <summary>
/// Reads an actor's latest snapshot <b>without activating the actor</b> (ADR-0048): the query-side
/// companion to <see cref="IActorState{TState}"/>. Safe on any instance — including ones that are
/// not the actor home — because it touches only the snapshot store, never a mailbox.
/// </summary>
/// <remarks>
/// The returned value is the last written snapshot, not the actor's live in-memory state: mutations
/// the actor has not yet persisted with <c>WriteStateAsync</c> are invisible here. That is the
/// deliberate contract — queries read durable truth, commands go through the actor. The reader runs
/// no actor code, so design <typeparamref name="TState"/> as <b>the query contract</b>: put
/// interpretation (constants, derived flags) and pure transitions on the record itself — the shared
/// type carries that logic to every deserialization site — and keep actor methods to
/// apply-write-side-effect. Interpretation left in actor methods is invisible to reader-based
/// queries and lets them silently diverge from facade queries. Freshness equals the actor's write
/// cadence: under write-through the reader is as fresh as any database query; under periodic
/// checkpointing it is bounded-stale (a warm-restart mechanism, not a live view) — real-time
/// observation of hot in-memory state is a <em>push</em> concern (client events published by the
/// actor and fanned out to every instance), never reader polling.
/// </remarks>
public interface IActorStateReader {
    /// <summary>Reads the latest snapshot for <paramref name="key"/>, or <see langword="null"/> when none is stored.</summary>
    ValueTask<TState?> ReadAsync<TState>(ActorSnapshotKey key, CancellationToken cancellationToken = default)
        where TState : class;
}

/// <summary>The default <see cref="IActorStateReader"/> over the registered snapshot store.</summary>
public sealed class ActorStateReader(IActorSnapshotStore store, IElarionJsonSerialization serialization)
    : IActorStateReader {
    /// <inheritdoc />
    public async ValueTask<TState?> ReadAsync<TState>(ActorSnapshotKey key, CancellationToken cancellationToken = default)
        where TState : class {
        var snapshot = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? null
            : JsonSerializer.Deserialize(snapshot.Payload, serialization.GetTypeInfo<TState>());
    }
}
