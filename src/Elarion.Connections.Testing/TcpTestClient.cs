using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Elarion.Connections.Tcp;

namespace Elarion.Connections.Testing;

/// <summary>
/// A framed TCP client for tests and device simulators: connects to an adapter endpoint (or is handed an
/// accepted socket when simulating the device side of a dialer), speaks whole messages through the same
/// <see cref="TcpMessageFramer"/> the endpoint uses, and exposes text conveniences for challenge/response
/// handshakes — the client every gateway simulator otherwise hand-rolls.
/// </summary>
public sealed class TcpTestClient : IAsyncDisposable {
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TcpMessageFramer _framer;
    private readonly ArrayBufferWriter<byte> _sendBuffer = new(4 * 1024);
    private byte[] _receiveBuffer = new byte[8 * 1024];
    private int _start;
    private int _end;

    private TcpTestClient(TcpClient client, TcpMessageFramer framer) {
        _client = client;
        _stream = client.GetStream();
        _framer = framer;
    }

    /// <summary>Connects to <paramref name="endPoint"/> (a listener under test).</summary>
    /// <param name="endPoint">The endpoint to dial.</param>
    /// <param name="framer">The endpoint's framing.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<TcpTestClient> ConnectAsync(
        EndPoint endPoint, TcpMessageFramer framer, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(framer);
        var client = new TcpClient { NoDelay = true };
        try {
            await client.Client.ConnectAsync(endPoint, ct);
            return new TcpTestClient(client, framer);
        }
        catch {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an already-connected socket (e.g. the accepted side of a fake device that an
    /// Elarion dialer connected to).</summary>
    /// <param name="client">The connected socket; the test client owns and disposes it.</param>
    /// <param name="framer">The endpoint's framing.</param>
    public static TcpTestClient FromConnected(TcpClient client, TcpMessageFramer framer) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(framer);
        return new TcpTestClient(client, framer);
    }

    /// <summary>Sends one framed message.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) {
        _sendBuffer.ResetWrittenCount();
        _framer.WriteMessage(payload.Span, _sendBuffer);
        await _stream.WriteAsync(_sendBuffer.WrittenMemory, ct);
    }

    /// <summary>Sends one framed UTF-8 text message.</summary>
    public ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        return SendAsync(Encoding.UTF8.GetBytes(message), ct);
    }

    /// <summary>Receives the next complete message (copied, safe to hold);
    /// <see langword="null"/> when the peer closed.</summary>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default) {
        while (true) {
            if (_end > _start && _framer.TryReadMessage(
                    _receiveBuffer.AsMemory(_start, _end - _start), out var consumed, out var message)) {
                var copy = message.ToArray();
                _start += consumed;
                return copy;
            }

            if (_start > 0) {
                Buffer.BlockCopy(_receiveBuffer, _start, _receiveBuffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _receiveBuffer.Length) {
                Array.Resize(ref _receiveBuffer, _receiveBuffer.Length * 2);
            }

            var read = await _stream.ReadAsync(_receiveBuffer.AsMemory(_end), ct);
            if (read == 0) {
                return null;
            }

            _end += read;
        }
    }

    /// <summary>Receives the next complete message decoded as UTF-8 text; <see langword="null"/> on close.</summary>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken ct = default) {
        var message = await ReceiveAsync(ct);
        return message is null ? null : Encoding.UTF8.GetString(message);
    }

    /// <summary>Closes the client side of the connection.</summary>
    public ValueTask DisposeAsync() {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
