namespace Elarion.Actors;

/// <summary>
/// A stored actor snapshot as returned by <see cref="IActorSnapshotStore.ReadAsync"/>: the
/// canonical-JSON payload plus the store's opaque concurrency tag for it.
/// </summary>
public sealed record ActorSnapshot {
    /// <summary>The snapshot payload — canonical-JSON text of the actor's state type.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// The store's opaque concurrency tag for this snapshot version. Passed back verbatim on the
    /// next write/clear so the store can reject a stale writer.
    /// </summary>
    public required string ETag { get; init; }
}
