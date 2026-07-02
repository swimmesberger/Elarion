namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Configures the background collector that reclaims expired tus upload sessions — both incomplete
/// sessions past their upload-expiry window and completed sessions past their completed-retention window.
/// </summary>
public sealed class TusGcOptions {
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
    /// that reaches its expiry right as a request arrives has a window to be resumed or read before it is
    /// reclaimed. Mirrors <c>BlobGcOptions.SafetyMargin</c>.
    /// </remarks>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a completed session row is retained after finalization before it is eligible for
    /// reclamation. Defaults to 1 hour.
    /// </summary>
    /// <remarks>
    /// A completed session must live long enough for a client's <c>HEAD</c> to fetch the
    /// <c>Elarion-Blob-Ref</c> header. Finalization stamps the row's expiry to <c>now + CompletedRetention</c>,
    /// after which the collector reaps it, so completed rows never grow the staging table unbounded.
    /// </remarks>
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromHours(1);
}
