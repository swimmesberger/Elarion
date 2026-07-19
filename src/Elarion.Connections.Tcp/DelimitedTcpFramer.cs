using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Delimiter framing: a message is the bytes between an optional start delimiter and a required end
/// delimiter — <c>new DelimitedTcpFramer(end: (byte)'\n')</c> is line framing, a start/end pair is the
/// classic device-telegram envelope. With a start delimiter, bytes before it are skipped as line noise
/// (serial-bridge reality); without one, the message starts where the last one ended.
/// </summary>
/// <remarks>
/// Delimiters are structural, so a payload cannot contain them: <see cref="WriteMessage"/> throws
/// <see cref="ArgumentException"/> when the payload contains the end delimiter (or the start delimiter,
/// when one is configured) — silently emitting it would make the peer parse one message as two, a silent
/// framing desync. A codec whose payloads may carry those bytes must escape/encode them (e.g. base64 or
/// hex on a line-framed link) or use <see cref="LengthPrefixedTcpFramer"/>.
/// </remarks>
/// <param name="end">The byte that terminates every message — must never occur inside a payload.</param>
/// <param name="start">The optional byte that opens every message (telegram envelope); when set, it must
/// never occur inside a payload either.</param>
public sealed class DelimitedTcpFramer(byte end, byte? start = null) : TcpMessageFramer {
    /// <inheritdoc />
    public override bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed,
        out ReadOnlyMemory<byte> message) {
        consumed = 0;
        message = default;
        var span = buffer.Span;

        var payloadStart = 0;
        if (start is { } startByte) {
            var startIndex = span.IndexOf(startByte);
            if (startIndex < 0) {
                // Pure line noise — consume it so it neither accumulates against the size cap nor gets
                // rescanned on every read.
                consumed = buffer.Length;
                return false;
            }

            // Drop the noise before the start delimiter even when the message is still incomplete
            // (overwritten with the full count below when a complete message is extracted).
            consumed = startIndex;

            payloadStart = startIndex + 1;
        }

        var endIndex = span[payloadStart..].IndexOf(end);
        if (endIndex < 0) return false; // consumed may carry pre-start noise dropped above

        consumed = payloadStart + endIndex + 1;
        message = buffer.Slice(payloadStart, endIndex);
        return true;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The payload contains the end delimiter (or the configured start
    /// delimiter) — unframeable without desyncing the peer; escape it in the codec instead.</exception>
    public override void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) {
        if (payload.IndexOf(end) >= 0)
            throw new ArgumentException(
                $"The payload contains the end delimiter 0x{end:X2} — delimiter framing cannot represent it "
                + "(the peer would parse one message as two). Escape/encode the payload in the codec, or use "
                + "length-prefixed framing.",
                nameof(payload));

        if (start is { } forbiddenStart && payload.IndexOf(forbiddenStart) >= 0)
            throw new ArgumentException(
                $"The payload contains the start delimiter 0x{forbiddenStart:X2} — delimiter framing cannot "
                + "represent it (a resynchronizing peer would misparse the message boundary). Escape/encode "
                + "the payload in the codec, or use length-prefixed framing.",
                nameof(payload));

        if (start is { } startByte) {
            var head = output.GetSpan(1);
            head[0] = startByte;
            output.Advance(1);
        }

        output.Write(payload);
        var tail = output.GetSpan(1);
        tail[0] = end;
        output.Advance(1);
    }
}
