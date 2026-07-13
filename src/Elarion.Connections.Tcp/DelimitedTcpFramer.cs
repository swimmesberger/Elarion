using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Delimiter framing: a message is the bytes between an optional start delimiter and a required end
/// delimiter — <c>new DelimitedTcpFramer(end: (byte)'\n')</c> is line framing, a start/end pair is the
/// classic device-telegram envelope. With a start delimiter, bytes before it are skipped as line noise
/// (serial-bridge reality); without one, the message starts where the last one ended.
/// </summary>
public sealed class DelimitedTcpFramer(byte end, byte? start = null) : TcpMessageFramer {
    /// <inheritdoc />
    public override bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed, out ReadOnlyMemory<byte> message) {
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
        if (endIndex < 0) {
            return false;   // consumed may carry pre-start noise dropped above
        }

        consumed = payloadStart + endIndex + 1;
        message = buffer.Slice(payloadStart, endIndex);
        return true;
    }

    /// <inheritdoc />
    public override void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) {
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
