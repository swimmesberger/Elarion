using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace Elarion.EntityFrameworkCore.Paging;

/// <summary>
/// Writes keyset column values into a compact binary buffer rendered as an opaque, URL-safe cursor
/// token. Generated keyset definitions call the typed <c>Write*</c> methods in keyset-column order;
/// <see cref="CursorReader"/> reads them back in the same order. The format carries no field names
/// or delimiters — the generated code is the schema — so encoding is reflection-free.
/// </summary>
/// <remarks>
/// Cursors are opaque to clients but are <b>not</b> a security boundary: they encode the boundary
/// row's key values and are neither signed nor encrypted. Handlers must still apply their own
/// authorization filters (e.g. <c>Where(c =&gt; c.OwnerId == user.UserId)</c>).
/// </remarks>
public sealed class CursorWriter
{
    internal const byte FormatVersion = 1;

    private readonly List<byte> _buffer = new(32) { FormatVersion };

    /// <summary>Appends a 32-bit signed integer (also used for narrower integral types).</summary>
    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a 64-bit signed integer (also used for other 64-bit integral types).</summary>
    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a double-precision floating point value.</summary>
    public void WriteDouble(double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a single-precision floating point value.</summary>
    public void WriteSingle(float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a decimal value.</summary>
    public void WriteDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        WriteInt32(bits[0]);
        WriteInt32(bits[1]);
        WriteInt32(bits[2]);
        WriteInt32(bits[3]);
    }

    /// <summary>Appends a GUID.</summary>
    public void WriteGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a UTF-8 string, length-prefixed.</summary>
    public void WriteString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt32((uint)byteCount);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, bytes);
        _buffer.AddRange(bytes);
    }

    /// <summary>Appends a <see cref="DateTime"/> preserving ticks and kind.</summary>
    public void WriteDateTime(DateTime value) => WriteInt64(value.ToBinary());

    /// <summary>Appends a <see cref="DateTimeOffset"/> by its UTC instant.</summary>
    public void WriteDateTimeOffset(DateTimeOffset value) => WriteInt64(value.UtcTicks);

    /// <summary>Appends a <see cref="DateOnly"/> value.</summary>
    public void WriteDateOnly(DateOnly value) => WriteInt32(value.DayNumber);

    /// <summary>Appends a <see cref="TimeOnly"/> value.</summary>
    public void WriteTimeOnly(TimeOnly value) => WriteInt64(value.Ticks);

    /// <summary>Renders the accumulated bytes as an opaque, URL-safe cursor token.</summary>
    public string ToCursor() => Base64Url.EncodeToString(CollectionsMarshal.AsSpan(_buffer));

    private void WriteVarUInt32(uint value)
    {
        while (value >= 0x80)
        {
            _buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        _buffer.Add((byte)value);
    }
}

/// <summary>
/// Reads keyset column values from a cursor token produced by <see cref="CursorWriter"/>. Every
/// <c>TryRead*</c> returns <c>false</c> on a truncated or malformed buffer; generated decoders treat
/// any failure (including trailing bytes via <see cref="AtEnd"/>) as an invalid cursor.
/// </summary>
public struct CursorReader
{
    private readonly byte[] _data;
    private int _position;

    private CursorReader(byte[] data)
    {
        _data = data;
        _position = 1; // skip the format-version byte
    }

    /// <summary>Whether every byte in the buffer has been consumed.</summary>
    public readonly bool AtEnd => _position == _data.Length;

    /// <summary>Attempts to decode <paramref name="cursor"/> into a reader positioned after the version byte.</summary>
    public static bool TryCreate(string? cursor, out CursorReader reader)
    {
        reader = default;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        byte[] data;
        try
        {
            data = Base64Url.DecodeFromChars(cursor.AsSpan());
        }
        catch (FormatException)
        {
            return false;
        }

        if (data.Length < 1 || data[0] != CursorWriter.FormatVersion)
        {
            return false;
        }

        reader = new CursorReader(data);
        return true;
    }

    /// <summary>Reads a 32-bit signed integer.</summary>
    public bool TryReadInt32(out int value)
    {
        if (!TryTake(sizeof(int), out var span))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(span);
        return true;
    }

    /// <summary>Reads a 64-bit signed integer.</summary>
    public bool TryReadInt64(out long value)
    {
        if (!TryTake(sizeof(long), out var span))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(span);
        return true;
    }

    /// <summary>Reads a double-precision floating point value.</summary>
    public bool TryReadDouble(out double value)
    {
        if (!TryTake(sizeof(double), out var span))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadDoubleLittleEndian(span);
        return true;
    }

    /// <summary>Reads a single-precision floating point value.</summary>
    public bool TryReadSingle(out float value)
    {
        if (!TryTake(sizeof(float), out var span))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(span);
        return true;
    }

    /// <summary>Reads a decimal value.</summary>
    public bool TryReadDecimal(out decimal value)
    {
        value = default;
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!TryReadInt32(out bits[i]))
            {
                return false;
            }
        }

        value = new decimal(bits);
        return true;
    }

    /// <summary>Reads a GUID.</summary>
    public bool TryReadGuid(out Guid value)
    {
        if (!TryTake(16, out var span))
        {
            value = default;
            return false;
        }

        value = new Guid(span);
        return true;
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    public bool TryReadString(out string value)
    {
        value = string.Empty;
        if (!TryReadVarUInt32(out var length) || !TryTake((int)length, out var span))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(span);
        return true;
    }

    /// <summary>Reads a <see cref="DateTime"/> preserving ticks and kind.</summary>
    public bool TryReadDateTime(out DateTime value)
    {
        if (!TryReadInt64(out var binary))
        {
            value = default;
            return false;
        }

        value = DateTime.FromBinary(binary);
        return true;
    }

    /// <summary>Reads a <see cref="DateTimeOffset"/> as a UTC instant.</summary>
    public bool TryReadDateTimeOffset(out DateTimeOffset value)
    {
        if (!TryReadInt64(out var ticks))
        {
            value = default;
            return false;
        }

        value = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>Reads a <see cref="DateOnly"/> value.</summary>
    public bool TryReadDateOnly(out DateOnly value)
    {
        if (!TryReadInt32(out var dayNumber))
        {
            value = default;
            return false;
        }

        value = DateOnly.FromDayNumber(dayNumber);
        return true;
    }

    /// <summary>Reads a <see cref="TimeOnly"/> value.</summary>
    public bool TryReadTimeOnly(out TimeOnly value)
    {
        if (!TryReadInt64(out var ticks))
        {
            value = default;
            return false;
        }

        value = new TimeOnly(ticks);
        return true;
    }

    private bool TryTake(int count, out ReadOnlySpan<byte> span)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            span = default;
            return false;
        }

        span = _data.AsSpan(_position, count);
        _position += count;
        return true;
    }

    private bool TryReadVarUInt32(out uint value)
    {
        value = 0;
        var shift = 0;
        while (shift < 35)
        {
            if (_position >= _data.Length)
            {
                return false;
            }

            var b = _data[_position++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}
