namespace Elarion.Actors;

/// <summary>
/// Snapshot-persisted state for an <c>[Actor]</c> class (ADR-0047). Declaring a constructor
/// parameter of this type gives the activation durable state: the runtime loads the latest
/// snapshot before <see cref="IActorLifecycle.OnActivateAsync"/> runs, and the actor persists
/// changes explicitly with <see cref="WriteStateAsync"/> — passivation never writes implicitly.
/// </summary>
/// <remarks>
/// The member surface deliberately mirrors Orleans' <c>IPersistentState&lt;TState&gt;</c>
/// (<c>State</c>/<c>RecordExists</c>/<c>Etag</c>/<c>ReadStateAsync</c>/<c>WriteStateAsync</c>/
/// <c>ClearStateAsync</c>), so actor state call sites survive an outgrown app's migration to a real
/// cluster unchanged (ADR-0042's migration seam). Differences are strict improvements that stay
/// call-site compatible: <see cref="ValueTask"/> returns, optional <see cref="CancellationToken"/>s,
/// and a nullable <see cref="State"/> instead of Orleans' default-constructed instance (Elarion state
/// types are <c>required</c>-property records without parameterless constructors).
/// Snapshots are serialized with Elarion's canonical JSON, so <typeparamref name="TState"/> must be
/// registered in a source-generated <c>JsonSerializerContext</c> that participates in the canonical
/// options (under reflection-off serialization a missing registration fails the activation loudly).
/// All members run under the actor's single-threaded guarantee — they must only be touched from the
/// actor's own turns (including <c>OnActivateAsync</c>/<c>OnDeactivateAsync</c>).
/// </remarks>
/// <typeparam name="TState">The persisted state type; a plain serializable record.</typeparam>
public interface IActorState<TState> where TState : class {
    /// <summary>
    /// The in-memory state. <see langword="null"/> until the actor assigns it when no snapshot
    /// exists yet (<see cref="RecordExists"/> is <see langword="false"/>); mutations become durable
    /// only through <see cref="WriteStateAsync"/>.
    /// </summary>
    TState? State { get; set; }

    /// <summary>Whether a stored snapshot backs this activation (loaded on activation or created by a write).</summary>
    bool RecordExists { get; }

    /// <summary>
    /// The store's opaque concurrency tag for the loaded snapshot, or <see langword="null"/> when
    /// none exists. Diagnostic surface only — the runtime round-trips it on writes automatically.
    /// </summary>
    string? Etag { get; }

    /// <summary>
    /// Re-reads the stored snapshot, replacing <see cref="State"/> (unwritten mutations are
    /// discarded; a since-deleted snapshot resets to the not-exists state). The runtime calls this
    /// once per activation before <see cref="IActorLifecycle.OnActivateAsync"/>; actors call it
    /// manually only to refresh deliberately.
    /// </summary>
    ValueTask ReadStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <see cref="State"/> as the new snapshot. Throws
    /// <see cref="ActorSnapshotConcurrencyException"/> when the stored snapshot changed underneath
    /// this activation — with a correctly single-homed actor that means two processes host the same
    /// actor key, a deployment misconfiguration that must surface, not be retried away.
    /// </summary>
    ValueTask WriteStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored snapshot (if any) and resets <see cref="State"/> to <see langword="null"/>.
    /// </summary>
    ValueTask ClearStateAsync(CancellationToken cancellationToken = default);
}
