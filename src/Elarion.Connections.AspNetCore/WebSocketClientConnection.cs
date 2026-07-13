using System.Net.WebSockets;
using System.Text;
using Elarion.Abstractions.Connections;

namespace Elarion.Connections.AspNetCore;

/// <summary>
/// One live WebSocket connection: the adapter's <see cref="IClientConnectionSink"/>. The raw send legs
/// (<see cref="SendTextAsync"/>/<see cref="SendBinaryAsync"/>, writes serialized — safe from any thread,
/// actor turn, or observer) are what proprietary codecs use directly; the neutral sink members delegate to
/// the connection's <see cref="IClientConnectionProtocol"/> so the wire encoding stays codec-owned.
/// </summary>
public sealed class WebSocketClientConnection : IClientConnectionSink {
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IClientConnectionProtocol? _protocol;

    internal WebSocketClientConnection(ClientConnection connection, WebSocket socket) {
        Connection = connection;
        _socket = socket;
    }

    /// <inheritdoc />
    public ClientConnection Connection { get; }

    internal void AttachProtocol(IClientConnectionProtocol protocol) => _protocol = protocol;

    internal IClientConnectionProtocol Protocol =>
        _protocol ?? throw new InvalidOperationException("The connection's protocol is not attached yet.");

    /// <summary>Sends one text frame; at-most-once, faults with
    /// <see cref="ClientConnectionClosedException"/> when the socket is gone.</summary>
    public ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        return SendCoreAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, ct);
    }

    /// <summary>Sends one binary frame; at-most-once, faults with
    /// <see cref="ClientConnectionClosedException"/> when the socket is gone.</summary>
    public ValueTask SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) =>
        SendCoreAsync(message, WebSocketMessageType.Binary, ct);

    /// <summary>Initiates a graceful close from the server side (sends the close frame; the receive loop
    /// completes the handshake and unregisters). Safe to call on an already-closed socket.</summary>
    public async ValueTask CloseAsync(CancellationToken ct = default) {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) {
            return;
        }

        try {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
        }
        catch (WebSocketException) {
            // The client raced us to the close — the receive loop observes it either way.
        }
        catch (ObjectDisposedException) {
        }
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
        return Protocol.InvokeAsync<TRequest, TResponse>(name, request, options, ct);
    }

    private async ValueTask SendCoreAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, CancellationToken ct) {
        if (_socket.State != WebSocketState.Open) {
            throw new ClientConnectionClosedException(Connection.ConnectionId);
        }

        await _sendLock.WaitAsync(ct);
        try {
            await _socket.SendAsync(payload, type, endOfMessage: true, ct);
        }
        catch (WebSocketException failure) {
            throw new ClientConnectionClosedException(Connection.ConnectionId, failure);
        }
        catch (ObjectDisposedException failure) {
            throw new ClientConnectionClosedException(Connection.ConnectionId, failure);
        }
        finally {
            _sendLock.Release();
        }
    }
}
