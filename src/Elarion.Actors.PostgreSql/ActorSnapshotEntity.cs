namespace Elarion.Actors.PostgreSql;

/// <summary>
/// The persisted row backing <see cref="Elarion.Actors.IActorSnapshotStore"/>: one snapshot per
/// actor activation identity, keyed <c>(ActorName, ActorKey)</c>. The payload is canonical-JSON
/// text stored as <c>jsonb</c>, so operators can inspect actor state with plain SQL.
/// </summary>
public sealed class ActorSnapshotEntity {
    /// <summary>The actor's logical name (the facade name, e.g. <c>OrderFulfillment</c>).</summary>
    public required string ActorName { get; init; }

    /// <summary>The activation key's canonical text (<c>"singleton"</c> for singleton actors).</summary>
    public required string ActorKey { get; init; }

    /// <summary>The snapshot payload — canonical-JSON text of the actor's state type.</summary>
    public string State { get; set; } = "";

    /// <summary>When the snapshot was last written.</summary>
    public DateTimeOffset UpdatedOnUtc { get; set; }

    /// <summary>
    /// The optimistic-concurrency version; minted lineage-unique (random) at create and
    /// incremented on every write, so a version from a cleared lineage never matches a re-created
    /// one (the ABA guard).
    /// </summary>
    public long Version { get; set; }
}
