using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Elarion.Abstractions.Connections;

namespace Elarion.Connections.Tcp;

/// <summary>
/// One live TCP connection: the adapter's <see cref="IClientConnectionSink"/>. The raw send legs
/// (<see cref="SendTextAsync"/>/<see cref="SendBinaryAsync"/>, writes
/// serialized — safe from any thread, actor turn, or observer) frame through the endpoint's configured
/// framer; the neutral sink members delegate to the connection's codec, identical to every other adapter.
/// </summary>
public sealed class TcpClientConnection : IClientConnectionSink {
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly TcpMessageFramer _framer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IClientConnectionProtocol? _protocol;

    internal TcpClientConnection(ClientConnection connection, TcpClient client, Stream stream, TcpMessageFramer framer) {
        Connection = connection;
        _client = client;
        _stream = stream;
        _framer = framer;
    }

    /// <inheritdoc />
    public ClientConnection Connection { get; }

    internal void AttachProtocol(IClientConnectionProtocol protocol) => _protocol = protocol;

    internal IClientConnectionProtocol Protocol =>
        _protocol ?? throw new InvalidOperationException("The connection's protocol is not attached yet.");

    /// <summary>Sends one framed UTF-8 text message (write-side sugar over <see cref="SendBinaryAsync"/>);
    /// at-most-once, faults with <see cref="ClientConnectionClosedException"/> when the link is gone.</summary>
    public ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        return SendBinaryAsync(Encoding.UTF8.GetBytes(message), ct);
    }

    /// <summary>Sends one message through the endpoint's framer; at-most-once, faults with
    /// <see cref="ClientConnectionClosedException"/> when the link is gone.</summary>
    public async ValueTask SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) {
        var writer = new ArrayBufferWriter<byte>();
        _framer.WriteMessage(message.Span, writer);

        await _sendLock.WaitAsync(ct);
        try {
            await _stream.WriteAsync(writer.WrittenMemory, ct);
        }
        catch (IOException failure) {
            throw new ClientConnectionClosedException(Connection.ConnectionId, failure);
        }
        catch (ObjectDisposedException failure) {
            throw new ClientConnectionClosedException(Connection.ConnectionId, failure);
        }
        finally {
            _sendLock.Release();
        }
    }

    /// <summary>Closes the connection from the server side; the receive loop observes it and unregisters.
    /// Safe to call on an already-closed link.</summary>
    public ValueTask CloseAsync() {
        try {
            _client.Close();
        }
        catch (Exception) {
            // Closing a dying socket is best-effort; teardown happens via the receive loop regardless.
        }

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
        return Protocol.InvokeAsync<TRequest, TResponse>(name, request, options, ct);
    }
}
