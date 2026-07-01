namespace Elarion.Blobs;

/// <summary>
/// Lifecycle state of a stored blob.
/// </summary>
/// <remarks>
/// The two states model the "pre-upload, then reference" pattern used when a file is uploaded
/// separately from the entity it will be attached to (for example over a JSON transport that cannot
/// carry binary). A <see cref="Pending"/> blob is uploaded but not yet referenced and is eligible for
/// time-to-live garbage collection; an application <em>promotes</em> it to <see cref="Committed"/>
/// (via <see cref="IBlobLifecycle.CommitAsync"/>) in the same unit of work that creates the referencing
/// entity, so an abandoned upload is reclaimed automatically while a referenced one is kept forever.
/// </remarks>
public enum BlobLifecycleState {
    /// <summary>
    /// Uploaded but not yet referenced by a durable entity. Eligible for time-to-live garbage
    /// collection once its expiry passes.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Referenced by a durable entity. Never garbage collected; its expiry is cleared.
    /// </summary>
    Committed = 1,
}
