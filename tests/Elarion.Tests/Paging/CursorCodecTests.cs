using AwesomeAssertions;
using Elarion.Paging;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class CursorCodecTests {
    private const uint Tag = 0x1234ABCDu;

    [Fact]
    public void RoundTrip_MixedColumns_PreservesValuesAndConsumesBuffer() {
        var createdAt = new DateTime(2026, 6, 19, 8, 30, 15, DateTimeKind.Utc);
        var id = Guid.Parse("11112222-3333-4444-5555-666677778888");
        const long sequence = 9_223_372_036_854L;
        const string name = "ünïcödé café";

        var writer = new CursorWriter(Tag);
        writer.WriteDateTime(createdAt);
        writer.WriteGuid(id);
        writer.WriteInt64(sequence);
        writer.WriteString(name);
        var cursor = writer.ToCursor();

        CursorReader.TryCreate(cursor, Tag, out var reader).Should().BeTrue();
        reader.TryReadDateTime(out var readCreatedAt).Should().BeTrue();
        reader.TryReadGuid(out var readId).Should().BeTrue();
        reader.TryReadInt64(out var readSequence).Should().BeTrue();
        reader.TryReadString(out var readName).Should().BeTrue();

        readCreatedAt.Should().Be(createdAt);
        readId.Should().Be(id);
        readSequence.Should().Be(sequence);
        readName.Should().Be(name);
        reader.AtEnd.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_NumericTypes_PreservesValues() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(int.MinValue);
        writer.WriteDouble(3.141592653589793);
        writer.WriteSingle(2.5f);
        writer.WriteDecimal(12345.6789m);
        writer.WriteDateOnly(new DateOnly(2026, 6, 19));
        writer.WriteTimeOnly(new TimeOnly(8, 30, 15));
        writer.WriteDateTimeOffset(new DateTimeOffset(2026, 6, 19, 8, 30, 15, TimeSpan.FromHours(2)));

        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();
        reader.TryReadInt32(out var i).Should().BeTrue();
        reader.TryReadDouble(out var d).Should().BeTrue();
        reader.TryReadSingle(out var f).Should().BeTrue();
        reader.TryReadDecimal(out var m).Should().BeTrue();
        reader.TryReadDateOnly(out var dateOnly).Should().BeTrue();
        reader.TryReadTimeOnly(out var timeOnly).Should().BeTrue();
        reader.TryReadDateTimeOffset(out var dto).Should().BeTrue();

        i.Should().Be(int.MinValue);
        d.Should().Be(3.141592653589793);
        f.Should().Be(2.5f);
        m.Should().Be(12345.6789m);
        dateOnly.Should().Be(new DateOnly(2026, 6, 19));
        timeOnly.Should().Be(new TimeOnly(8, 30, 15));
        dto.Should().Be(new DateTimeOffset(2026, 6, 19, 8, 30, 15, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void TryCreate_MalformedCursor_ReturnsFalse() {
        CursorReader.TryCreate("not a real cursor!!", Tag, out _).Should().BeFalse();
        CursorReader.TryCreate("", Tag, out _).Should().BeFalse();
        CursorReader.TryCreate(null, Tag, out _).Should().BeFalse();
    }

    [Fact]
    public void TryCreate_WrongKeysetTag_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(42);
        var cursor = writer.ToCursor();

        // A cursor minted by one keyset (Tag) must not decode against a different keyset's tag,
        // otherwise its bytes would be reinterpreted as a garbage seek position.
        CursorReader.TryCreate(cursor, Tag, out _).Should().BeTrue();
        CursorReader.TryCreate(cursor, Tag + 1, out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_TruncatedBuffer_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(42);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadInt32(out _).Should().BeTrue();
        reader.TryReadInt32(out _).Should().BeFalse();
        reader.AtEnd.Should().BeTrue();
    }

    [Fact]
    public void TryReadString_HugeVarintLength_ReturnsFalse() {
        // Declared length uint.MaxValue casts to a negative int; the reader must report malformed,
        // never attempt the slice.
        CursorReader.TryCreate(Adversarial(0xFF, 0xFF, 0xFF, 0xFF, 0x0F), Tag, out var reader)
            .Should().BeTrue();

        reader.TryReadString(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadString_LengthOverflowingPositionCheck_ReturnsFalse() {
        // Declared length int.MaxValue over a tiny buffer: a naive "position + count > length" bounds
        // check overflows negative and passes, and the slice then throws. The reader must return false.
        CursorReader.TryCreate(Adversarial(0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x61), Tag, out var reader)
            .Should().BeTrue();

        reader.TryReadString(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDateTime_OutOfRangeBits_ReturnsFalse() {
        // long.MaxValue decodes to a tick count past DateTime.MaxValue; FromBinary would throw.
        var writer = new CursorWriter(Tag);
        writer.WriteInt64(long.MaxValue);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadDateTime(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDateTimeOffset_OutOfRangeTicks_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt64(long.MaxValue);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadDateTimeOffset(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadTimeOnly_NegativeTicks_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt64(-1);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadTimeOnly(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadTimeOnly_TicksPastOneDay_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt64(TimeOnly.MaxValue.Ticks + 1);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadTimeOnly(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDateOnly_OutOfRangeDayNumber_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(int.MaxValue);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadDateOnly(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDateOnly_NegativeDayNumber_ReturnsFalse() {
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(-1);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadDateOnly(out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDecimal_InvalidFlagsBits_ReturnsFalse() {
        // The fourth int is the decimal's flags word; an arbitrary bit pattern is invalid and the
        // decimal constructor would throw.
        var writer = new CursorWriter(Tag);
        writer.WriteInt32(1);
        writer.WriteInt32(2);
        writer.WriteInt32(3);
        writer.WriteInt32(-1);
        CursorReader.TryCreate(writer.ToCursor(), Tag, out var reader).Should().BeTrue();

        reader.TryReadDecimal(out _).Should().BeFalse();
    }

    /// <summary>Builds a cursor whose header is valid for <see cref="Tag"/> followed by raw adversarial payload bytes.</summary>
    private static string Adversarial(params byte[] payload) {
        var header = System.Buffers.Text.Base64Url.DecodeFromChars(new CursorWriter(Tag).ToCursor().AsSpan());
        return System.Buffers.Text.Base64Url.EncodeToString([.. header, .. payload]);
    }
}
