using AwesomeAssertions;
using Elarion.Spreadsheets;
using Xunit;
using static Elarion.Tests.Spreadsheets.HandBuiltWorkbook;

namespace Elarion.Tests.Spreadsheets;

/// <summary>The scenarios under Values typed by hand are read leniently (FR-037 to FR-046).</summary>
public sealed class SpreadsheetLenientReadingTests {
    private static async Task<SpreadsheetReadResult<LenientRow>> ReadColumnAsync(
        string header, IEnumerable<string> cells, SpreadsheetReadOptions? options = null) {
        var rows = TextRow(1, header) + string.Concat(cells.Select((cell, i) => $"<row r=\"{i + 2}\">{cell}</row>"));
        return await SpreadsheetReaderRecordTests.ReadAsync<LenientRow>(Build(rows), options);
    }

    private static Task<SpreadsheetReadResult<LenientRow>> ReadTextsAsync(
        string header, IEnumerable<string> texts, SpreadsheetReadOptions? options = null) {
        return ReadColumnAsync(header, texts.Select((text, i) => Text($"A{i + 2}", text)), options);
    }

    [Fact]
    public async Task DecimalTypedWithEitherSeparator_ReadsAsItsValue_AndAMalformedGroupingIsAnError() {
        var result = await ReadTextsAsync("Amount", ["1.234,56", "1,234.56", "1234,56", "1234.56", "1 234,56", "1.234.567", "1.23.4", "-0,5", "1.234"]);

        result.Rows.Select(row => row.Record?.Amount).Should().Equal(1234.56m, 1234.56m, 1234.56m, 1234.56m, 1234.56m, 1234567m, null, -0.5m, 1.234m);
        result.Errors.Should().ContainSingle().Which.CellReference.Should().Be("A8");
    }

    [Fact]
    public async Task DateTypedByHand_ReadsAsTheDateItStates() {
        var result = await ReadTextsAsync("On", ["24.11.2026", "24.11.26", "1.2.2026", "2026-11-24", "24/11/2026"]);

        result.Rows.Select(row => row.Record?.On).Should().Equal(
            new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 24), new DateOnly(2026, 2, 1), new DateOnly(2026, 11, 24), null);
    }

    [Fact]
    public async Task DateFormatsOfTheReadOptions_AreReadToo() {
        var result = await ReadTextsAsync("At", ["11/24/2026 10:00", "24.11.2026 10:00:30", "2026-11-24T10:00"],
            new SpreadsheetReadOptions { DateFormats = ["M/d/yyyy HH:mm"] });

        result.HasErrors.Should().BeFalse();
        result.Records.Select(row => row.At).Should().Equal(
            new DateTime(2026, 11, 24, 10, 0, 0), new DateTime(2026, 11, 24, 10, 0, 30), new DateTime(2026, 11, 24, 10, 0, 0));
    }

    [Fact]
    public async Task BooleanWords_ReadAsTheirBoolean_PlusTheWordsTheApplicationAdds() {
        var options = new SpreadsheetReadOptions { TrueWords = ["ja", "j"], FalseWords = ["nein"] };
        var result = await ReadTextsAsync("Flag", ["x", "yes", "1", "true", "no", "0", "false", "JA", "j", "Nein", "maybe"], options);
        var numbers = await ReadColumnAsync("Flag", [Number("A2", "2"), Number("A3", "0"), "<c r=\"A4\" t=\"b\"><v>1</v></c>"]);

        result.Rows.Select(row => row.Record?.Flag).Should().Equal(true, true, true, true, false, false, false, true, true, false, null);
        numbers.Records.Select(row => row.Flag).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task NumbersInATextColumn_ReadAsTheirDigits_WithoutAnExponent() {
        var result = await ReadColumnAsync("Text", [Number("A2", "1010"), Number("A3", "1234567890123456"), Number("A4", "1E+20"), Number("A5", "0.1")]);

        result.Records.Select(row => row.Text).Should().Equal("1010", "1234567890123460", "100000000000000000000", "0.1");
    }

    [Fact]
    public async Task NumberStoredAsTheNearestBinaryFraction_ReadsAsTheDecimalTheSpreadsheetShows() {
        var result = await ReadColumnAsync("Amount", [Number("A2", "1234.5599999999999"), Number("A3", "0.30000000000000004")]);

        result.Records.Select(row => row.Amount).Should().Equal(1234.56m, 0.3m);
    }

    [Fact]
    public async Task DateTimeOffsetColumn_ReadsOnlyIsoTextThatStatesAnOffset() {
        var result = await ReadColumnAsync("Instant", [
            Text("A2", "2026-11-24T10:00:00+01:00"), Number("A3", "46350", 1), Text("A4", "24.11.2026 10:00"), Text("A5", "2026-11-24T09:00:00Z")
        ]);

        result.Rows.Select(row => row.Record?.Instant).Should().Equal(
            new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(1)), null, null, new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
        result.Errors.Should().HaveCount(2).And.OnlyContain(error =>
            error.Code == SpreadsheetErrorCodes.InvalidValue && error.ExpectedKind == SpreadsheetValueKind.DateTimeOffset);
    }

    [Fact]
    public async Task TimeColumn_ReadsATimeCellOrTypedTime_AndRejectsADayOrMore() {
        var result = await ReadColumnAsync("Time", [Number("A2", "0.4375", 3), Text("A3", "7:05"), Number("A4", "1.5", 3)]);

        result.Rows.Select(row => row.Record?.Time).Should().Equal(new TimeOnly(10, 30), new TimeOnly(7, 5), null);
    }

    [Fact]
    public async Task GuidAndCustomDateFormatCells_AreRead() {
        var id = Guid.CreateVersion7();
        var result = await SpreadsheetReaderRecordTests.ReadAsync<LenientRow>(Build(
            TextRow(1, "Id", "On") + $"<row r=\"2\">{Text("A2", id.ToString())}{Number("B2", "46350", 2)}</row>"));

        result.Records.Single().Id.Should().Be(id);
        result.Records.Single().On.Should().Be(new DateOnly(2026, 11, 24));
    }

    [Fact]
    public async Task ErrorCell_IsAnInvalidValue() {
        var result = await ReadColumnAsync("Amount", ["<c r=\"A2\" t=\"e\"><v>#DIV/0!</v></c>"]);

        result.Errors.Should().ContainSingle().Which.Message.Should().Be("'#DIV/0!' is not a number.");
    }
}
