using System.Buffers;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Delimiter framing for text protocols: a message is the bytes between an optional start delimiter and a
/// required end delimiter — <c>new DelimitedTextTcpFramer(end: (byte)'\n')</c> is line framing, a
/// start/end pair is the classic device-telegram envelope. With a start delimiter, bytes before it are
/// skipped as line noise (serial-bridge reality); without one, the message starts where the last one ended.
/// </summary>
public sealed class DelimitedTextTcpFramer(byte end, byte? start = null) : TcpMessageFramer {
    /// <inheritdoc />
    public override bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed, out TcpFramedMessage message) {
        consumed = 0;
        message = default;
        var span = buffer.Span;

        var payloadStart = 0;
        if (start is { } startByte) {
            var startIndex = span.IndexOf(startByte);
            if (startIndex < 0) {
                return false;
            }

            payloadStart = startIndex + 1;
        }

        var endIndex = span[payloadStart..].IndexOf(end);
        if (endIndex < 0) {
            return false;
        }

        consumed = payloadStart + endIndex + 1;
        message = new TcpFramedMessage(TcpMessageKind.Text, buffer.Slice(payloadStart, endIndex));
        return true;
    }

    /// <inheritdoc />
    public override void WriteMessage(TcpFramedMessage message, IBufferWriter<byte> output) {
        if (start is { } startByte) {
            var head = output.GetSpan(1);
            head[0] = startByte;
            output.Advance(1);
        }

        output.Write(message.Payload.Span);
        var tail = output.GetSpan(1);
        tail[0] = end;
        output.Advance(1);
    }
}
