namespace Elarion.Actors;

/// <summary>
/// Thrown when a snapshot write or clear observes a different stored version than the activation
/// loaded (ADR-0047). With the single-node actor runtime this means another process hosts the same
/// actor key (or something mutated the snapshot row out of band) — a deployment problem to surface,
/// not a transient condition to retry.
/// </summary>
public sealed class ActorSnapshotConcurrencyException(ActorSnapshotKey key, string? expectedETag)
    : InvalidOperationException(
        $"Snapshot of actor '{key.ActorName}' ({key.Key}) changed underneath this activation "
        + $"(expected {(expectedETag is null ? "no stored snapshot" : $"version '{expectedETag}'")}). "
        + "Most likely two processes host the same actor key — the in-memory actor runtime is single-node by design.") {
    /// <summary>The snapshot the conflicting operation targeted.</summary>
    public ActorSnapshotKey Key { get; } = key;

    /// <summary>The ETag the activation expected; <see langword="null"/> when it expected to create the snapshot.</summary>
    public string? ExpectedETag { get; } = expectedETag;
}
