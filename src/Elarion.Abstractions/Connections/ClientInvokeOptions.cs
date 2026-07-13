namespace Elarion.Abstractions.Connections;

/// <summary>
/// Per-call options for <see cref="IClientConnectionSink.InvokeAsync"/>. An options bag rather than
/// parameters so the seam widens without signature churn.
/// </summary>
public sealed record ClientInvokeOptions {
    /// <summary>
    /// How long to await the client's reply before the invoke faults with <see cref="TimeoutException"/>.
    /// The effective timeout layers: this per-call value wins when set; <see langword="null"/> falls back
    /// to the adapter's configured default (<c>ElarionConnectionsOptions.DefaultInvokeTimeout</c>,
    /// 30 seconds out of the box); only a default explicitly configured to <see langword="null"/> leaves
    /// the wait bounded by nothing but the caller's cancellation token and the connection's lifetime.
    /// Pass <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to make a single call unbounded
    /// without reconfiguring the default.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
