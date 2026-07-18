using System.Text;
using Elarion.Abstractions.Connections;

namespace Elarion.Connections.Tcp;

/// <summary>
/// One live TCP connection: the adapter's <see cref="IClientConnectionSink"/>. The raw send legs
/// (<see cref="SendTextAsync"/>/<see cref="SendBinaryAsync"/>) go through the connection's bounded FIFO
/// outbound writer — safe from any thread, actor turn, or observer; a completed send means the complete
/// frame was physically written to the stream. The neutral sink members delegate to the connection's
/// codec, identical to every other adapter.
/// </summary>
public sealed class TcpClientConnection : IClientConnectionSink {
    private readonly TcpOutboundWriter _writer;
    private readonly TcpConnectionLifetime _lifetime;
    private readonly TimeSpan? _defaultInvokeTimeout;
    private IClientConnectionProtocol? _protocol;

    internal TcpClientConnection(
        ClientConnection connection, TcpOutboundWriter writer, TcpConnectionLifetime lifetime,
        TimeSpan? defaultInvokeTimeout) {
        ConnectionState = new ClientConnectionState(connection);
        _writer = writer;
        _lifetime = lifetime;
        _defaultInvokeTimeout = defaultInvokeTimeout;
    }

    /// <inheritdoc />
    public ClientConnectionState ConnectionState { get; }

    /// <inheritdoc />
    public ClientConnection Connection => ConnectionState.Current;

    internal void AttachProtocol(IClientConnectionProtocol protocol) => _protocol = protocol;

    internal IClientConnectionProtocol Protocol =>
        _protocol ?? throw new InvalidOperationException("The connection's protocol is not attached yet.");

    /// <summary>Sends one framed UTF-8 text message (write-side sugar over <see cref="SendBinaryAsync"/>);
    /// at-most-once, faults with <see cref="ClientConnectionClosedException"/> when the link is gone.</summary>
    public ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        return SendBinaryAsync(Encoding.UTF8.GetBytes(message), ct);
    }

    /// <summary>
    /// Sends one message through the endpoint's framer via the connection's bounded FIFO writer. The send
    /// completes only after the complete frame was written to the stream. Faults with
    /// <see cref="ClientConnectionClosedException"/> when the link is gone,
    /// <see cref="TcpOutboundFrameTooLargeException"/> when framing exceeds
    /// <see cref="ElarionTcpConnectionOptions.MaxOutboundFrameBytes"/> (rejected before any byte is
    /// written; the connection stays open), or <see cref="TcpSendQueueFullException"/> when
    /// <see cref="ElarionTcpConnectionOptions.MaxPendingSends"/> sends are already admitted (deterministic
    /// saturation — nothing was queued). Cancellation before the frame reaches the stream withdraws it;
    /// cancellation during the physical write aborts the connection, because a partial frame may have
    /// corrupted stream boundaries.
    /// </summary>
    public ValueTask SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) =>
        _writer.SendAsync(message, ct);

    /// <summary>Requests a graceful server-side close: no new sends are admitted, admitted sends drain,
    /// and the connection tears down once (codec close, unregistration, disposal). Safe to call on an
    /// already-closed link.</summary>
    public ValueTask CloseAsync() {
        _lifetime.RequestGracefulClose(null);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SendAsync<TPayload>(string name, TPayload payload, CancellationToken ct = default)
        where TPayload : class {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(payload);
        return Protocol.SendAsync(name, payload, ct);
    }

    /// <inheritdoc />
    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        string name, TRequest request, ClientInvokeOptions? options = null, CancellationToken ct = default)
        where TRequest : class {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        return Protocol.InvokeAsync<TRequest, TResponse>(
            name, request, options.WithDefaultTimeout(_defaultInvokeTimeout), ct);
    }
}
