namespace Elarion.Blobs;

/// <summary>
/// Configures the background collector that reclaims expired staged-upload sessions — both incomplete
/// sessions past their upload-expiry window and completed sessions past their completed-retention window.
/// </summary>
public sealed class StagedUploadGcOptions {
    /// <summary>How long the collector waits between sweeps. Defaults to 5 minutes.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum number of sessions deleted per sweep. Defaults to 200.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// A grace period added to each session's expiry before it is eligible for deletion. Defaults to
    /// 1 minute.
    /// </summary>
    /// <remarks>
    /// The collector deletes sessions whose expiry is older than <c>now - SafetyMargin</c>, so a session
    /// that reaches its expiry right as a request arrives has a window to be resumed or read before it
    /// is reclaimed. Mirrors <see cref="BlobGcOptions.SafetyMargin"/>.
    /// </remarks>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMinutes(1);
}
