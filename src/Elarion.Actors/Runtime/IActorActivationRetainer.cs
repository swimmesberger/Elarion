namespace Elarion.Actors.Runtime;

/// <summary>
/// The cell-side seam behind <c>ActorWorkItem.RetainActivation</c> (ADR-0052 refCount lifetime): a live
/// stream enumeration retains its activation so <b>idle</b> passivation never ends a stream mid-flight.
/// Correctness passivations are deliberately unaffected — a snapshot-conflicted activation still dies
/// (its state is stale), and shutdown still drains — which is why the streaming actor keeps the
/// complete-the-hub-on-deactivate obligation for those paths.
/// </summary>
internal interface IActorActivationRetainer {
    /// <summary>Increments the retention count; disposing the result releases it (idempotent).</summary>
    IDisposable Retain();
}
