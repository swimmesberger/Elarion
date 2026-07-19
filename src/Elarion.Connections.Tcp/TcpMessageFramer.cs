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
/// Endpoint-level framers are shared by every connection, so implementations must be stateless and
/// thread-safe. A negotiated or otherwise stateful framing scheme belongs in a per-connection
/// <see cref="TcpConnectionSettings.Framer"/> override. <see cref="TryReadMessage"/> is called with
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
    /// <param name="message">The extracted <b>payload only</b> — it already excludes the header/prefix/
    /// delimiter framing (<paramref name="consumed"/> accounts for those wire bytes; do not subtract them
    /// from this slice again). May slice <paramref name="buffer"/> and is only valid until the adapter's
    /// next read.</param>
    public abstract bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed,
        out ReadOnlyMemory<byte> message);

    /// <summary>
    /// Writes <paramref name="payload"/> in wire framing onto <paramref name="output"/>.
    /// <paramref name="payload"/> is the codec's <b>payload only</b> — the framer prepends/appends its own
    /// framing (length prefix, reserved prologue, delimiter); the payload never arrives pre-framed.
    /// </summary>
    public abstract void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);

    /// <summary>
    /// Begins one in-place framed message (ADR-0066's writer-based send): emit or reserve the frame's
    /// prologue on <paramref name="output"/> and return its byte length. The caller then serializes the
    /// payload directly onto <paramref name="output"/> and finishes with <see cref="CompleteMessage"/> —
    /// the zero-copy inverse of <see cref="WriteMessage"/>, which copies from a caller-materialized buffer.
    /// </summary>
    /// <param name="output">The framed output the prologue is written to.</param>
    /// <returns>The prologue length in bytes (0 when the framing has no prologue).</returns>
    public abstract int BeginMessage(IBufferWriter<byte> output);

    /// <summary>
    /// Completes an in-place framed message: backfill the reserved prologue (e.g. the length prefix, now
    /// that the payload length is known), validate the payload where the framing demands it (delimiter
    /// occurrence), and append any epilogue to <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="prologue"/> and <paramref name="payload"/> view the writer's backing memory: they are
    /// invalidated by the first write to <paramref name="output"/>, so implementations must finish reading
    /// and backfilling both spans before appending an epilogue. Throwing (an unrepresentable payload, like a
    /// delimiter occurrence) fails the send without ending the connection, exactly like a
    /// <see cref="WriteMessage"/> failure.
    /// </remarks>
    /// <param name="prologue">The prologue bytes <see cref="BeginMessage"/> reserved, for backfill.</param>
    /// <param name="payload">The payload the caller serialized in place, for validation.</param>
    /// <param name="output">The framed output any epilogue is appended to.</param>
    public abstract void CompleteMessage(Span<byte> prologue, ReadOnlySpan<byte> payload, IBufferWriter<byte> output);
}
