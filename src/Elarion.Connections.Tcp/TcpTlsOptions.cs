using System.Net.Security;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Common policy for upgrading a raw TCP connection to TLS before the application framer or authenticator
/// can observe bytes.
/// </summary>
public abstract record TcpTlsOptions {
    /// <summary>
    /// Maximum duration of the TLS handshake (default 10 seconds). This is separate from
    /// <see cref="ElarionTcpConnectionOptions.HandshakeTimeout"/>, which bounds the subsequent framed
    /// application authentication.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>TLS policy for an accepted listener connection.</summary>
public sealed record TcpServerTlsOptions : TcpTlsOptions {
    /// <summary>
    /// Creates fresh BCL server authentication options for one accepted peer. The factory runs after TCP
    /// establishment but before any byte is read or written by the application framer. Certificate and client-
    /// certificate policy remain explicit BCL configuration; platform validation is never bypassed by Elarion.
    /// </summary>
    public required Func<TcpConnectionPeer, CancellationToken, ValueTask<SslServerAuthenticationOptions>>
        CreateAuthenticationOptionsAsync { get; init; }
}

/// <summary>TLS policy for one dial-out connection.</summary>
public sealed record TcpClientTlsOptions : TcpTlsOptions {
    /// <summary>
    /// Creates fresh BCL client authentication options for one remote peer. Set
    /// <see cref="SslClientAuthenticationOptions.TargetHost"/> to the certificate identity expected by this
    /// binding. Platform certificate validation remains fail-closed unless the application explicitly supplies
    /// a validation callback; test-only bypasses belong in test configuration.
    /// </summary>
    public required Func<TcpConnectionPeer, CancellationToken, ValueTask<SslClientAuthenticationOptions>>
        CreateAuthenticationOptionsAsync { get; init; }
}
