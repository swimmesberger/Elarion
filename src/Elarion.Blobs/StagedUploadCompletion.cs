namespace Elarion.Blobs;

/// <summary>
/// Details for completing a staged upload — the two expiry stamps completion writes. Both are
/// completion-time-relative, which is why they ride the call rather than the creation.
/// </summary>
public sealed record StagedUploadCompletion {
    /// <summary>
    /// When the completed session record may be reclaimed. A completed session must outlive the
    /// completion response so a client can still fetch the produced blob reference (for example over a
    /// tus <c>HEAD</c>); after this instant the garbage collector reaps the record.
    /// </summary>
    public required DateTimeOffset SessionExpiresAt { get; init; }

    /// <summary>
    /// When the produced <see cref="BlobLifecycleState.Pending"/> blob may be garbage collected unless
    /// an application commits it via <see cref="IBlobLifecycle.CommitAsync"/>, or <c>null</c> for no
    /// expiry (the blob is never reclaimed automatically).
    /// </summary>
    public DateTimeOffset? BlobExpiresAt { get; init; }
}
