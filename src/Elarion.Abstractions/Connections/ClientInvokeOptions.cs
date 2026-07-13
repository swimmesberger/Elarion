namespace Elarion.Abstractions.Connections;

/// <summary>
/// Per-call options for <see cref="IClientConnectionSink.InvokeAsync"/>. An options bag rather than
/// parameters so the seam widens without signature churn.
/// </summary>
public sealed record ClientInvokeOptions {
    /// <summary>
    /// How long to await the client's reply before the invoke faults with <see cref="TimeoutException"/>.
    /// <see langword="null"/> uses the adapter's configured default; a server→client invoke is never
    /// unbounded — a client that answers nothing must surface as a fault, not a hung turn.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
