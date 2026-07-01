namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Configures the background collector that reclaims expired, never-completed tus upload sessions.
/// </summary>
public sealed class TusGcOptions {
    /// <summary>How long the collector waits between sweeps. Defaults to 5 minutes.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum number of sessions deleted per sweep. Defaults to 200.</summary>
    public int BatchSize { get; set; } = 200;
}
