using AwesomeAssertions;
using Elarion.Paging;
using Xunit;

namespace Elarion.Tests.Paging;

public sealed class CursorCodecTests
{
    [Fact]
    public void RoundTrip_MixedColumns_PreservesValuesAndConsumesBuffer()
    {
        var createdAt = new DateTime(2026, 6, 19, 8, 30, 15, DateTimeKind.Utc);
        var id = Guid.Parse("11112222-3333-4444-5555-666677778888");
        const long sequence = 9_223_372_036_854L;
        const string name = "ünïcödé café";

        var writer = new CursorWriter();
        writer.WriteDateTime(createdAt);
        writer.WriteGuid(id);
        writer.WriteInt64(sequence);
        writer.WriteString(name);
        var cursor = writer.ToCursor();

        CursorReader.TryCreate(cursor, out var reader).Should().BeTrue();
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
    public void RoundTrip_NumericTypes_PreservesValues()
    {
        var writer = new CursorWriter();
        writer.WriteInt32(int.MinValue);
        writer.WriteDouble(3.141592653589793);
        writer.WriteSingle(2.5f);
        writer.WriteDecimal(12345.6789m);
        writer.WriteDateOnly(new DateOnly(2026, 6, 19));
        writer.WriteTimeOnly(new TimeOnly(8, 30, 15));
        writer.WriteDateTimeOffset(new DateTimeOffset(2026, 6, 19, 8, 30, 15, TimeSpan.FromHours(2)));

        CursorReader.TryCreate(writer.ToCursor(), out var reader).Should().BeTrue();
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
    public void TryCreate_MalformedCursor_ReturnsFalse()
    {
        CursorReader.TryCreate("not a real cursor!!", out _).Should().BeFalse();
        CursorReader.TryCreate("", out _).Should().BeFalse();
        CursorReader.TryCreate(null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_TruncatedBuffer_ReturnsFalse()
    {
        var writer = new CursorWriter();
        writer.WriteInt32(42);
        CursorReader.TryCreate(writer.ToCursor(), out var reader).Should().BeTrue();

        reader.TryReadInt32(out _).Should().BeTrue();
        reader.TryReadInt32(out _).Should().BeFalse();
        reader.AtEnd.Should().BeTrue();
    }
}
