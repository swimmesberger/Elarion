namespace Elarion.Actors.Runtime;

/// <summary>
/// The cell-side seam a running turn talks to: activation retention for live streams and the
/// snapshot-conflict mark.
/// </summary>
internal interface IActorActivation {
    /// <summary>
    /// Increments the retention count; disposing the result releases it (idempotent). Behind
    /// <c>ActorWorkItem.RetainActivation</c> (ADR-0052 refCount lifetime): a live stream enumeration
    /// retains its activation so <b>idle</b> passivation never ends a stream mid-flight. Correctness
    /// passivations are deliberately unaffected — a snapshot-conflicted activation still dies (its state
    /// is stale), and shutdown still drains — which is why the streaming actor keeps the
    /// complete-the-hub-on-deactivate obligation for those paths.
    /// </summary>
    IDisposable Retain();

    /// <summary>
    /// Marks the activation stale after one of its own state slots raised
    /// <see cref="ActorSnapshotConcurrencyException"/> (ADR-0047). The turn calls this before its caller
    /// can observe the outcome: from then on no turn starts on this activation, so a caller that saw the
    /// conflict, and every call queued behind it, reaches a fresh activation that reloads the snapshot.
    /// </summary>
    void MarkSnapshotConflicted();
}
