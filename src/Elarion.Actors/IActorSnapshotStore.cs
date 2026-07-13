namespace Elarion.Actors;

/// <summary>
/// The provider seam behind <see cref="IActorState{TState}"/> (ADR-0047): a keyed snapshot store
/// holding one canonical-JSON payload per <see cref="ActorSnapshotKey"/>, guarded by an opaque
/// ETag so a stale activation can never overwrite a newer snapshot unnoticed. Implementations are
/// registered as singletons and must be safe for concurrent use across actors.
/// </summary>
/// <remarks>
/// The payload is canonical-JSON <em>text</em> by contract — the runtime owns serialization, the
/// store owns durability and concurrency. ETags are store-opaque: the runtime only round-trips
/// them. <strong>ETags must be lineage-unique</strong>: a create must never mint an ETag that was
/// previously observable for the same key, so that after a clear + re-create a stale writer still
/// holding a tag from the dead lineage always fails with
/// <see cref="ActorSnapshotConcurrencyException"/> instead of silently overwriting the new lineage
/// (the ABA guard behind "a lost write can never happen unnoticed"). A version-counter store
/// satisfies this by starting each lineage at a freshly minted random value rather than a
/// constant. Every operation is a full-snapshot operation; incremental/event-sourced persistence
/// is a deliberate non-goal of this seam.
/// </remarks>
public interface IActorSnapshotStore {
    /// <summary>Reads the latest snapshot, or <see langword="null"/> when none is stored.</summary>
    ValueTask<ActorSnapshot?> ReadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="payload"/> as the new snapshot and returns its new ETag.
    /// <paramref name="expectedETag"/> is <see langword="null"/> to create (the key must not have a
    /// snapshot yet) or the previously observed tag to replace. A mismatch — the stored snapshot
    /// was created, replaced, or cleared by someone else — throws
    /// <see cref="ActorSnapshotConcurrencyException"/>. A create mints a lineage-unique ETag (see
    /// the interface remarks): tags from a cleared lineage must never match the re-created one.
    /// </summary>
    ValueTask<string> WriteAsync(
        ActorSnapshotKey key,
        string payload,
        string? expectedETag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the snapshot whose ETag is <paramref name="expectedETag"/>. A mismatch (including an
    /// already-deleted snapshot) throws <see cref="ActorSnapshotConcurrencyException"/>.
    /// </summary>
    ValueTask ClearAsync(ActorSnapshotKey key, string expectedETag, CancellationToken cancellationToken = default);
}
