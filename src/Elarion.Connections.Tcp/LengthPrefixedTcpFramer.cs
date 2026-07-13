using System.Buffers;
using System.Buffers.Binary;

namespace Elarion.Connections.Tcp;

/// <summary>
/// Binary framing: a 4-byte big-endian unsigned length prefix followed by the payload.
/// </summary>
public sealed class LengthPrefixedTcpFramer : TcpMessageFramer {
    private const int PrefixLength = 4;

    /// <inheritdoc />
    public override bool TryReadMessage(ReadOnlyMemory<byte> buffer, out int consumed, out ReadOnlyMemory<byte> message) {
        consumed = 0;
        message = default;
        if (buffer.Length < PrefixLength) {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer.Span);
        if (length > int.MaxValue - PrefixLength) {
            throw new InvalidOperationException($"Length prefix {length} is not a valid message length.");
        }

        var total = PrefixLength + (int)length;
        if (buffer.Length < total) {
            return false;
        }

        consumed = total;
        message = buffer.Slice(PrefixLength, (int)length);
        return true;
    }

    /// <inheritdoc />
    public override void WriteMessage(ReadOnlySpan<byte> payload, IBufferWriter<byte> output) {
        var span = output.GetSpan(PrefixLength);
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)payload.Length);
        output.Advance(PrefixLength);
        output.Write(payload);
    }
}
