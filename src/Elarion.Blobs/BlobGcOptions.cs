namespace Elarion.Blobs;

/// <summary>
/// Configures the background garbage collector that reclaims expired, never-committed pending blobs.
/// </summary>
public sealed class BlobGcOptions {
    /// <summary>
    /// How long the collector waits between sweeps when nothing is expiring. Defaults to 5 minutes.
    /// </summary>
    /// <remarks>
    /// When a sweep deletes a full <see cref="BatchSize"/> it keeps draining without waiting, so this
    /// bounds idle latency only.
    /// </remarks>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum number of blobs deleted per sweep. Defaults to 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// A grace period added to each blob's expiry before it is eligible for deletion. Defaults to
    /// 1 minute.
    /// </summary>
    /// <remarks>
    /// The collector deletes blobs whose expiry is older than <c>now - SafetyMargin</c>, giving a blob
    /// uploaded right at its expiry a window to be committed before it is reclaimed.
    /// </remarks>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromMinutes(1);
}
