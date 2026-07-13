namespace Elarion.Connections;

/// <summary>
/// Kernel-wide connection options shared by every adapter, configured via
/// <c>AddElarionConnections(o => …)</c>. Per-endpoint and per-connection knobs stay on the adapters;
/// only behavior every sink must apply identically lives here.
/// </summary>
public sealed class ElarionConnectionsOptions {
    /// <summary>
    /// The invoke timeout adapters apply when a call carries no per-call
    /// <see cref="Elarion.Abstractions.Connections.ClientInvokeOptions.Timeout"/>, so
    /// <c>IClientConnectionSink.InvokeAsync</c> is bounded by default — a client that never answers
    /// surfaces as a <see cref="TimeoutException"/>, never a silently hung await. Defaults to 30 seconds
    /// (aligned with the actor call-timeout backstop). Set <see langword="null"/> to apply no default:
    /// an invoke without a per-call timeout is then bounded only by the caller's cancellation token and
    /// the connection's lifetime.
    /// </summary>
    public TimeSpan? DefaultInvokeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
