using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>
/// The framing seam that turns a TCP byte stream into the complete messages the codec seam expects (TCP has
/// no message boundaries — this is the one thing a TCP adapter owns that a WebSocket adapter gets from its
/// protocol). Framing is <b>boundaries only</b>: on TCP, bytes are just bytes, so every inbound message is
/// delivered to the codec's <c>OnBinaryAsync</c> as a raw slice — a text protocol's codec decodes with one
/// <c>Encoding.UTF8.GetString</c>, and only the codecs that want a string pay for one. Ship-with framers:
/// <see cref="LengthPrefixedTcpFramer"/> and <see cref="DelimitedTcpFramer"/>.
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
    /// <param name="consumed">Bytes to drop from the head of the buffer. May be non-zero even when
    /// returning <see langword="false"/>: skippable noise (bytes that can never begin a message) must be
    /// consumed so it neither accumulates against the size cap nor gets rescanned per read.</param>
    /// <param name="message">The extracted payload; it may slice <paramref name="buffer"/> and is only
    /// valid until the adapter's next read.</param>
    public abstract bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed, out ReadOnlyMemory<byte> message);

    /// <summary>Writes <paramref name="payload"/> in wire framing onto <paramref name="output"/>.</summary>
    public abstract void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);
}
