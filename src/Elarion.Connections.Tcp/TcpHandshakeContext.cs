using System.Buffers;
using System.Net;
using System.Text;

namespace Elarion.Connections.Tcp;

/// <summary>
/// What an authenticator sees during a TCP handshake: the endpoints (for binding-configured identity —
/// device protocols often have no credentials, only "who is allowed to connect here"), plus framed message
/// IO for challenge/response exchanges. No connection exists yet; nothing is registered until the
/// authenticator returns a ticket.
/// </summary>
public sealed class TcpHandshakeContext {
    private readonly Stream _stream;
    private readonly TcpMessageFramer _framer;
    private readonly TcpMessageReader _reader;

    internal TcpHandshakeContext(
        Stream stream, TcpMessageFramer framer, TcpMessageReader reader,
        EndPoint? remoteEndPoint, EndPoint? localEndPoint) {
        _stream = stream;
        _framer = framer;
        _reader = reader;
        RemoteEndPoint = remoteEndPoint;
        LocalEndPoint = localEndPoint;
    }

    /// <summary>The peer's endpoint (identity-by-binding protocols key their ticket off this).</summary>
    public EndPoint? RemoteEndPoint { get; }

    /// <summary>The local endpoint the connection arrived on (or dialed out from).</summary>
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Receives the next framed message payload; <see langword="null"/> when the peer closed
    /// instead of answering (treat as a rejected handshake).</summary>
    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken ct = default) => _reader.ReadAsync(ct);

    /// <summary>Receives the next framed message decoded as UTF-8 text; <see langword="null"/> on close.</summary>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken ct = default) {
        var message = await _reader.ReadAsync(ct);
        return message is null ? null : Encoding.UTF8.GetString(message.Value.Span);
    }

    /// <summary>Sends one framed message (e.g. the challenge nonce).</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) {
        var writer = new ArrayBufferWriter<byte>();
        _framer.WriteMessage(payload.Span, writer);
        await _stream.WriteAsync(writer.WrittenMemory, ct);
    }

    /// <summary>Sends one framed UTF-8 text message.</summary>
    public ValueTask SendTextAsync(string message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(message);
        return SendAsync(Encoding.UTF8.GetBytes(message), ct);
    }
}
