using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Serialization;

namespace Elarion.Actors.Runtime;

/// <summary>A state instance created during activation that the cell loads before the first turn.</summary>
internal interface IActorStateSlot {
    ValueTask LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The runtime <see cref="IActorState{TState}"/>: canonical-JSON (de)serialization over the
/// registered <see cref="IActorSnapshotStore"/>, with the store's ETag tracked per activation.
/// Confined to the actor's turns, so no synchronization.
/// </summary>
internal sealed class ActorState<TState>(
    ActorSnapshotKey key,
    IActorSnapshotStore store,
    IElarionJsonSerialization serialization,
    ActorStateTracker tracker) : IActorState<TState>, IActorStateSlot
    where TState : class {
    private JsonTypeInfo<TState>? _typeInfo;

    public TState? State { get; set; }

    public bool RecordExists => Etag is not null;

    public string? Etag { get; private set; }

    private JsonTypeInfo<TState> TypeInfo => _typeInfo ??= serialization.GetTypeInfo<TState>();

    ValueTask IActorStateSlot.LoadAsync(CancellationToken cancellationToken) {
        return ReadStateAsync(cancellationToken);
    }

    public async ValueTask ReadStateAsync(CancellationToken cancellationToken = default) {
        var snapshot = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) {
            State = null;
            Etag = null;
            return;
        }

        State = JsonSerializer.Deserialize(snapshot.Payload, TypeInfo)
                ?? throw new InvalidOperationException(
                    $"Snapshot of actor '{key.ActorName}' ({key.Key}) deserialized to null.");
        Etag = snapshot.ETag;
    }

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken = default) {
        var state = State ?? throw new InvalidOperationException(
            $"Actor '{key.ActorName}' ({key.Key}) has no state to write; assign State before calling WriteStateAsync.");
        var payload = JsonSerializer.Serialize(state, TypeInfo);
        try {
            Etag = await store.WriteAsync(key, payload, Etag, cancellationToken).ConfigureAwait(false);
        }
        catch (ActorSnapshotConcurrencyException ex) {
            MarkOrigin(ex);
            throw;
        }
    }

    public async ValueTask ClearStateAsync(CancellationToken cancellationToken = default) {
        if (Etag is { } etag) {
            try {
                await store.ClearAsync(key, etag, cancellationToken).ConfigureAwait(false);
            }
            catch (ActorSnapshotConcurrencyException ex) {
                MarkOrigin(ex);
                throw;
            }

            Etag = null;
        }

        State = null;
    }

    // Stamps the conflict with this activation's tracker so the turn's transparent retry
    // (ADR-0047) only fires for the activation whose own slot raised it — a conflict re-thrown out
    // of a nested actor call must fault the outer turn, never re-run it.
    private void MarkOrigin(ActorSnapshotConcurrencyException exception) {
        exception.Origin = tracker;
    }
}
