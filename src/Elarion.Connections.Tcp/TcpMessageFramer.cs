using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>The kind a framed message carries — decides which codec inbound member receives it.</summary>
public enum TcpMessageKind {
    /// <summary>Delivered to the codec's <c>OnTextAsync</c> (UTF-8 decoded).</summary>
    Text,

    /// <summary>Delivered to the codec's <c>OnBinaryAsync</c>.</summary>
    Binary,
}

/// <summary>One framed message. The payload memory is only valid until the adapter's next read — copy it to
/// keep it.</summary>
public readonly record struct TcpFramedMessage(TcpMessageKind Kind, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The framing seam that turns a TCP byte stream into the complete messages the codec seam expects (TCP has
/// no message boundaries — this is the one thing a TCP adapter owns that a WebSocket adapter gets from its
/// protocol). Ship-with framers: <see cref="LengthPrefixedTcpFramer"/> (binary) and
/// <see cref="DelimitedTextTcpFramer"/> (delimiter-framed text, the classic device-telegram shape).
/// </summary>
/// <remarks>
/// Implementations are stateless with respect to the buffer: <see cref="TryReadMessage"/> is called with
/// everything received-but-unconsumed and either extracts exactly one complete leading message (reporting
/// the consumed byte count, which may exceed the payload for delimiters/headers) or returns
/// <see langword="false"/> for "need more data". A framer that detects an unrecoverable stream corruption
/// should throw — the adapter closes the connection.
/// </remarks>
public abstract class TcpMessageFramer {
    /// <summary>Tries to extract one complete message from the head of <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Everything received and not yet consumed.</param>
    /// <param name="consumed">Bytes to drop from the head of the buffer (0 when returning <see langword="false"/>).</param>
    /// <param name="message">The extracted message; its payload may slice <paramref name="buffer"/>.</param>
    public abstract bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed, out TcpFramedMessage message);

    /// <summary>Writes <paramref name="message"/> in wire framing onto <paramref name="output"/>.</summary>
    public abstract void WriteMessage(TcpFramedMessage message, IBufferWriter<byte> output);
}
