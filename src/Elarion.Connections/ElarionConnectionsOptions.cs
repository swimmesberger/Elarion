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

    /// <summary>
    /// Maximum number of adapter-owned identity metadata entries accepted at registration or promotion.
    /// Defaults to 32. Metadata is operational context, not an unbounded application payload.
    /// </summary>
    public int MaxIdentityMetadataEntries { get; set; } = 32;

    /// <summary>Maximum UTF-16 character count of one identity metadata key. Defaults to 128.</summary>
    public int MaxIdentityMetadataKeyLength { get; set; } = 128;

    /// <summary>Maximum UTF-16 character count of one identity metadata value. Defaults to 1,024.</summary>
    public int MaxIdentityMetadataValueLength { get; set; } = 1024;

    /// <summary>Maximum number of identities in one connection principal. Defaults to 16.</summary>
    public int MaxPrincipalIdentities { get; set; } = 16;

    /// <summary>
    /// Maximum claims across the principal and every actor identity. Defaults to 256.
    /// </summary>
    public int MaxPrincipalClaims { get; set; } = 256;

    /// <summary>Maximum actor-identity nesting depth. Defaults to 16.</summary>
    public int MaxPrincipalActorDepth { get; set; } = 16;
}
