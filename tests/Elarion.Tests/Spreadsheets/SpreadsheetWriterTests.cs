using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using DocumentFormat.OpenXml.Spreadsheet;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

public sealed class SpreadsheetWriterTests {
    private static readonly ContactRow Anna = new() {
        Name = "Anna",
        Phone = "+43 1 234",
        Email = "anna@example.com",
        CustomerSince = new DateOnly(2026, 11, 24),
        Balance = 1234.5m,
        Visits = 7,
        Newsletter = true,
        Kind = ContactKind.Organization,
        Active = false
    };

    private static readonly ContactRow Bert = new() { Name = "Bert", Kind = ContactKind.Authority, Active = true };

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Export_HoldsTheHeadersThenOneRowPerRecord_WithNumbersDatesAndBooleansAsSuch() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna, Bert]);

        var rows = workbook.Rows();
        rows.Should().HaveCount(3);
        rows.Select(row => row.RowIndex!.Value).Should().Equal(1u, 2u, 3u);
        rows[0].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal(
            "Name", "E-mail", "Phone", "Customer since", "Balance", "Visits", "Newsletter", "Kind", "Active",
            "NameLength");

        var name = workbook.Cell("A2");
        name.DataType!.Value.Should().Be(CellValues.InlineString);
        WrittenWorkbook.Text(name).Should().Be("Anna");

        var balance = workbook.Cell("E2");
        balance.DataType.Should().BeNull("a number cell carries no type");
        balance.CellValue!.Text.Should().Be("1234.5");
        WrittenWorkbook.Text(workbook.Cell("F2")).Should().Be("7");

        var since = workbook.Cell("D2");
        since.DataType.Should().BeNull();
        since.CellValue!.Text.Should().Be("46350");
        workbook.CellFormat(since).NumberFormatId!.Value.Should().Be(14u);

        var newsletter = workbook.Cell("G2");
        newsletter.DataType!.Value.Should().Be(CellValues.Boolean);
        newsletter.CellValue!.Text.Should().Be("1");
        workbook.Cell("G3").CellValue!.Text.Should().Be("0");
    }

    [Fact]
    public async Task ColumnNameAndDisplayName_AreTheHeaders_AndOrderMakesTheColumnSecond() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna]);

        WrittenWorkbook.Text(workbook.Cell("B1")).Should().Be("E-mail");
        WrittenWorkbook.Text(workbook.Cell("B2")).Should().Be("anna@example.com");
        WrittenWorkbook.Text(workbook.Cell("D1")).Should().Be("Customer since");
    }

    [Fact]
    public async Task IgnoredProperty_HasNoColumn() {
        var record = new ContactRow { Name = "Anna", InternalNote = "do not export" };

        using var workbook = await WrittenWorkbook.WriteAsync([record]);

        workbook.Rows()[0].Elements<Cell>().Should().HaveCount(10);
        workbook.PartXml("xl/worksheets/sheet1.xml").Should().NotContain("do not export");
    }

    [Fact]
    public async Task DateColumnsWithoutFormat_UseTheViewersOwnDateAndTimeNotation() {
        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow()]);

        // Built-in formats 14, 22 and 21 have no format code in the file: the viewer's locale supplies it.
        workbook.CellFormat(workbook.Cell("F2")).NumberFormatId!.Value.Should().Be(22u);
        workbook.CellFormat(workbook.Cell("G2")).NumberFormatId!.Value.Should().Be(14u);
        workbook.CellFormat(workbook.Cell("H2")).NumberFormatId!.Value.Should().Be(21u);
        workbook.CustomFormatCode(14).Should().BeNull();
    }

    [Fact]
    public async Task AmountColumnWithFormat_UsesThatNumberFormat() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna]);

        var balance = workbook.Cell("E2");
        var format = workbook.CellFormat(balance);
        format.ApplyNumberFormat!.Value.Should().BeTrue();
        workbook.CustomFormatCode(format.NumberFormatId!.Value).Should().Be("#,##0.00");
        balance.CellValue!.Text.Should().Be("1234.5");
    }

    [Fact]
    public async Task DateTimeOffset_IsIso8601TextWithTheOffset() {
        var records = new[] {
            new ValueRow(),
            new ValueRow { Instant = new DateTimeOffset(2026, 11, 24, 10, 0, 0, 500, TimeSpan.FromHours(-5)) }
        };

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        var instant = workbook.Cell("I2");
        instant.DataType!.Value.Should().Be(CellValues.InlineString);
        WrittenWorkbook.Text(instant).Should().Be("2026-11-24T10:00:00+01:00");
        WrittenWorkbook.Text(workbook.Cell("I3")).Should().Be("2026-11-24T10:00:00.5000000-05:00");
    }

    [Fact]
    public async Task TextStartingWithEquals_IsTextAndNeverAFormula() {
        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow { Text = "=SUM(A1:A9)" }]);

        var text = workbook.Cell("A2");
        text.DataType!.Value.Should().Be(CellValues.InlineString);
        WrittenWorkbook.Text(text).Should().Be("=SUM(A1:A9)");
        text.CellFormula.Should().BeNull();
        workbook.PartXml("xl/worksheets/sheet1.xml").Should().NotContain("<f>").And.NotContain("<f ");
    }

    [Fact]
    public async Task HeaderRow_IsBoldOnALightFillWithAThinBottomBorder_AndStaysInPlace() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna]);

        var stylesheet = workbook.WorkbookPart.WorkbookStylesPart!.Stylesheet!;
        foreach (var header in workbook.Rows()[0].Elements<Cell>()) {
            var format = workbook.CellFormat(header);
            stylesheet.Fonts!.Elements<Font>().ElementAt((int)format.FontId!.Value).Bold.Should().NotBeNull();
            var fill = stylesheet.Fills!.Elements<Fill>().ElementAt((int)format.FillId!.Value).PatternFill!;
            fill.PatternType!.Value.Should().Be(PatternValues.Solid);
            fill.ForegroundColor!.Rgb!.Value.Should().Be("FFEEF2F7");
            stylesheet.Borders!.Elements<Border>().ElementAt((int)format.BorderId!.Value)
                .BottomBorder!.Style!.Value.Should().Be(BorderStyleValues.Thin);
        }

        var pane = workbook.Worksheet().Descendants<Pane>().Single();
        pane.State!.Value.Should().Be(PaneStateValues.Frozen);
        pane.VerticalSplit!.Value.Should().Be(1);
        pane.TopLeftCell!.Value.Should().Be("A2");
    }

    [Fact]
    public async Task ColumnsWithoutWidth_AreAsWideAsTheirHeaderAndValues() {
        var records = new[] {
            new WidthRow { Short = "abc", Long = "x", Fixed = "y", Huge = new string('h', 500) },
            new WidthRow { Short = "abcdefghijkl" }
        };

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        var columns = workbook.Columns();
        columns.Select(column => column.Width!.Value).Should().Equal(
            "abcdefghijkl".Length + 2d, "A rather long header".Length + 2d, 30d, 80d);
        columns.Should().OnlyContain(column => column.CustomWidth!.Value);
    }

    [Fact]
    public async Task ColumnWidths_MeasureOnlyTheSampledRecords() {
        var records = new[] { new WidthRow { Short = "abc" }, new WidthRow { Short = new string('s', 40) } };

        using var workbook = await WrittenWorkbook.WriteAsync(records, new SpreadsheetWriteOptions { WidthSampleRows = 1 });

        workbook.Columns()[0].Width!.Value.Should().Be("Short".Length + 2d);
        workbook.Rows().Should().HaveCount(3);
    }

    [Fact]
    public async Task SameRecordsWrittenTwice_AreByteIdentical() {
        using var first = await WrittenWorkbook.WriteAsync([Anna, Bert]);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), CancellationToken);
        using var second = await WrittenWorkbook.WriteAsync([Anna, Bert]);

        second.Bytes.Should().Equal(first.Bytes);
        first.EntryNames().Should().NotContain(name => name.StartsWith("docProps/", StringComparison.Ordinal));
        using var archive = new ZipArchive(new MemoryStream(first.Bytes), ZipArchiveMode.Read);
        archive.Entries.Should().OnlyContain(entry => entry.LastWriteTime.DateTime == new DateTime(1980, 1, 1));
    }

    [Fact]
    public async Task ControlCharacterAndEscapeLikeText_AreEscapedAndDecodeUnchanged() {
        const string text = "a\u0001b\rc_x0041_d￿e😀f";

        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow { Text = text }]);

        var xml = workbook.PartXml("xl/worksheets/sheet1.xml");
        xml.Should().Contain("a_x0001_b_x000D_c_x005F_x0041_d_xFFFF_e😀f");
        DecodeEscapes(WrittenWorkbook.Text(workbook.Cell("A2"))).Should().Be(text);
    }

    [Fact]
    public async Task TextWithSurroundingSpaces_KeepsThem() {
        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow { Text = "  padded " }]);

        WrittenWorkbook.Text(workbook.Cell("A2")).Should().Be("  padded ");
        workbook.PartXml("xl/worksheets/sheet1.xml").Should().Contain("<t xml:space=\"preserve\">  padded </t>");
    }

    [Fact]
    public async Task ComputedGetOnlyProperty_HasAColumn() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna]);

        WrittenWorkbook.Text(workbook.Cell("J1")).Should().Be("NameLength");
        WrittenWorkbook.Text(workbook.Cell("J2")).Should().Be("4");
    }

    [Fact]
    public async Task EnumAndLabelledBoolean_AreWrittenAsTheirLabels() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna, Bert]);

        WrittenWorkbook.Text(workbook.Cell("H2")).Should().Be("Company");
        WrittenWorkbook.Text(workbook.Cell("H3")).Should().Be("Authority");
        WrittenWorkbook.Text(workbook.Cell("I2")).Should().Be("Nein");
        WrittenWorkbook.Text(workbook.Cell("I3")).Should().Be("Ja");
        workbook.Cell("I2").DataType!.Value.Should().Be(CellValues.InlineString);
    }

    [Fact]
    public async Task NumbersOfEveryKind_AreWrittenInInvariantRoundTripNotation() {
        var record = new ValueRow { Amount = 0.1 + 0.2, Ratio = 0.1f, Count = -42, Big = ulong.MaxValue };

        using var workbook = await WrittenWorkbook.WriteAsync([record]);

        workbook.Cell("B2").CellValue!.Text.Should().Be((0.1 + 0.2).ToString("R", CultureInfo.InvariantCulture));
        workbook.Cell("C2").CellValue!.Text.Should().Be("0.1");
        workbook.Cell("D2").CellValue!.Text.Should().Be("-42");
        workbook.Cell("E2").CellValue!.Text.Should().Be("18446744073709551615");
    }

    [Fact]
    public async Task NullValue_IsAnEmptyCell() {
        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow()]);

        workbook.FindCell("K2").Should().BeNull("an empty number cell without a style is left out");
        var text = workbook.Cell("A2");
        text.CellValue.Should().BeNull();
        text.InlineString.Should().BeNull();
    }

    [Fact]
    public async Task DatesInJanuaryAndFebruary1900_FollowTheLeapYearCorrection() {
        var records = new[] {
            new ValueRow { On = new DateOnly(1900, 1, 1) },
            new ValueRow { On = new DateOnly(1900, 2, 28) },
            new ValueRow { On = new DateOnly(1900, 3, 1) },
            new ValueRow { On = new DateOnly(2026, 11, 24), At = new DateTime(2026, 11, 24, 18, 0, 0) }
        };

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        workbook.Cell("G2").CellValue!.Text.Should().Be("1");
        workbook.Cell("G3").CellValue!.Text.Should().Be("59");
        workbook.Cell("G4").CellValue!.Text.Should().Be("61");
        workbook.Cell("G5").CellValue!.Text.Should().Be("46350");
        workbook.Cell("F5").CellValue!.Text.Should().Be("46350.75");
        workbook.Cell("H5").CellValue!.Text.Should().Be((10.5 / 24).ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DateTimeOfAnyKind_IsWrittenAsItsDateAndTime() {
        var wallClock = new DateTime(2026, 11, 24, 10, 30, 0);
        var records = new[] {
            new ValueRow { At = DateTime.SpecifyKind(wallClock, DateTimeKind.Utc) },
            new ValueRow { At = DateTime.SpecifyKind(wallClock, DateTimeKind.Local) },
            new ValueRow { At = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified) }
        };

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        var expected = (46350 + 10.5 / 24).ToString("R", CultureInfo.InvariantCulture);
        workbook.Cell("F2").CellValue!.Text.Should().Be(expected);
        workbook.Cell("F3").CellValue!.Text.Should().Be(expected);
        workbook.Cell("F4").CellValue!.Text.Should().Be(expected);
    }

    [Fact]
    public async Task TextColumns_CarryTheTextFormatOnWrittenRowsAndOnTheEmptyRowsBelow() {
        using var workbook = await WrittenWorkbook.WriteAsync([Anna, new ContactRow { Name = "Bert" }]);

        workbook.CellFormat(workbook.Cell("A2")).NumberFormatId!.Value.Should().Be(49u);
        workbook.CellFormat(workbook.Cell("B3")).NumberFormatId!.Value.Should().Be(49u, "an empty text cell keeps the format");
        workbook.CellFormat(workbook.Cell("H2")).NumberFormatId!.Value.Should().Be(49u, "a choice column is text too");
        var columns = workbook.Columns();
        workbook.CellFormatAt(columns[0].Style!.Value).NumberFormatId!.Value.Should().Be(49u);
        workbook.CellFormatAt(columns[1].Style!.Value).NumberFormatId!.Value.Should().Be(49u);
        columns[5].Style.Should().BeNull("a number column keeps the default format");
    }

    [Fact]
    public async Task DefaultDateTime_FailsTheWriteNamingTheRowAndTheColumn() {
        var records = new[] { new ValueRow(), new ValueRow { At = default } };

        var act = () => SpreadsheetWorkbook.WriteAsync(Stream.Null, records, cancellationToken: CancellationToken).AsTask();

        var exception = (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which;
        exception.RowNumber.Should().Be(3);
        exception.Header.Should().Be("At");
        exception.CellReference.Should().Be("F3");
        exception.Worksheet.Should().Be("ValueRow");
        exception.Message.Should().Contain("row 3").And.Contain("'At'").And.Contain("F3");
    }

    [Fact]
    public async Task NaN_FailsTheWriteNamingTheRowAndTheColumn() {
        var act = () => SpreadsheetWorkbook
            .WriteAsync(Stream.Null, [new ValueRow { Amount = double.NaN }], cancellationToken: CancellationToken).AsTask();

        var exception = (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which;
        exception.RowNumber.Should().Be(2);
        exception.Header.Should().Be("Amount");
    }

    [Fact]
    public async Task Infinity_FailsTheWrite() {
        var act = () => SpreadsheetWorkbook
            .WriteAsync(Stream.Null, [new ValueRow { Ratio = float.PositiveInfinity }], cancellationToken: CancellationToken)
            .AsTask();

        (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which.Header.Should().Be("Ratio");
    }

    [Fact]
    public async Task DateBefore1900_FailsTheWrite() {
        var act = () => SpreadsheetWorkbook
            .WriteAsync(Stream.Null, [new ValueRow { On = new DateOnly(1899, 12, 31) }], cancellationToken: CancellationToken)
            .AsTask();

        (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which.CellReference.Should().Be("G2");
    }

    [Fact]
    public async Task TextLongerThan32767Characters_FailsTheWrite() {
        var records = Enumerable.Range(0, 300).Select(_ => new ValueRow { Text = "ok" })
            .Append(new ValueRow { Text = new string('x', 32_768) });

        var act = () => SpreadsheetWorkbook.WriteAsync(Stream.Null, records, cancellationToken: CancellationToken).AsTask();

        var exception = (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which;
        exception.RowNumber.Should().Be(302, "a record after the measured sample fails the same way");
        exception.CellReference.Should().Be("A302");
    }

    [Fact]
    public async Task TextOf32767Characters_IsWritten() {
        using var workbook = await WrittenWorkbook.WriteAsync([new ValueRow { Text = new string('x', 32_767) }]);

        WrittenWorkbook.Text(workbook.Cell("A2")).Should().HaveLength(32_767);
    }

    [Fact]
    public async Task RowBeyondTheLastRowOfAWorksheet_FailsTheWrite() {
        var records = Enumerable.Range(0, 1_048_576).Select(_ => new DataRow());

        var act = () => SpreadsheetWorkbook.WriteAsync(Stream.Null, records, cancellationToken: CancellationToken).AsTask();

        var exception = (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which;
        exception.RowNumber.Should().Be(1_048_577);
        exception.Worksheet.Should().Be("Data");
    }

    [Fact]
    public async Task TwoWorksheetsNamedDataAndData_FailTheWriteNamingTheWorksheet() {
        await using var workbook = SpreadsheetWorkbook.Create(new MemoryStream());
        await workbook.AddWorksheetAsync([new DataRow { Value = "1" }], cancellationToken: CancellationToken);

        var act = () => workbook.AddWorksheetAsync([new LowerDataRow { Value = "2" }], cancellationToken: CancellationToken)
            .AsTask();

        var exception = (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which;
        exception.Worksheet.Should().Be("data");
        exception.Message.Should().Contain("'data'");
    }

    [Fact]
    public async Task TwoRecordTypes_GiveOneWorksheetEach_InTheOrderWritten_NamedAsDeclared() {
        using var stream = new MemoryStream();
        await using (var writer = SpreadsheetWorkbook.Create(stream)) {
            await writer.AddWorksheetAsync([Anna], cancellationToken: CancellationToken);
            await writer.AddWorksheetAsync(
                [new BankAccountRow { Iban = "AT61 1904 3002 3457 3201", Holder = "Anna" }],
                cancellationToken: CancellationToken);
        }

        using var workbook = WrittenWorkbook.From(stream.ToArray());
        workbook.WorksheetNames.Should().Equal("Contacts", "Bank accounts");
        WrittenWorkbook.Text(workbook.Cell("A2", worksheet: 1)).Should().Be("AT61 1904 3002 3457 3201");
        workbook.Worksheet(1).Descendants<Pane>().Single().State!.Value.Should().Be(PaneStateValues.Frozen);
    }

    [Fact]
    public async Task WorksheetOptions_RenameTheWorksheet_AndAnInvalidNameIsRefused() {
        using var stream = new MemoryStream();
        await using (var writer = SpreadsheetWorkbook.Create(stream)) {
            await writer.AddWorksheetAsync(
                [Anna], new SpreadsheetWorksheetOptions { Name = "Kunden" }, CancellationToken);
        }

        using var workbook = WrittenWorkbook.From(stream.ToArray());
        workbook.WorksheetNames.Should().Equal("Kunden");

        await using var invalid = SpreadsheetWorkbook.Create(new MemoryStream());
        var act = () => invalid.AddWorksheetAsync(
            [Anna], new SpreadsheetWorksheetOptions { Name = "Q1/Q2" }, CancellationToken).AsTask();
        (await act.Should().ThrowAsync<SpreadsheetWriteException>()).Which.Message.Should().Contain("'/'");
    }

    [Fact]
    public async Task NoRecords_GiveTheHeaderRow() {
        using var workbook = await WrittenWorkbook.WriteAsync(Array.Empty<ContactRow>());

        workbook.Rows().Should().ContainSingle();
        workbook.Columns().Should().HaveCount(10);
    }

    [Fact]
    public async Task WorkbookWithoutAWorksheet_FailsWhenDisposed() {
        var workbook = SpreadsheetWorkbook.Create(new MemoryStream());

        var act = () => workbook.DisposeAsync().AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordsProducedOneAtATime_StreamToANonSeekableDestinationBeforeTheLastOneIsProduced() {
        var destination = new ForwardOnlyStream();
        long bytesBeforeLastRecord = -1;

        async IAsyncEnumerable<BankAccountRow> Produce([EnumeratorCancellation] CancellationToken cancellationToken = default) {
            const int count = 20_000;
            for (var i = 0; i < count; i++) {
                await Task.Yield();
                if (i == count - 1) bytesBeforeLastRecord = destination.Written;
                yield return new BankAccountRow { Iban = Guid.CreateVersion7().ToString("N"), Holder = i.ToString(CultureInfo.InvariantCulture) };
            }
        }

        await SpreadsheetWorkbook.WriteAsync(destination, Produce(CancellationToken), cancellationToken: CancellationToken);

        bytesBeforeLastRecord.Should().BeGreaterThan(0);
        using var workbook = WrittenWorkbook.From(destination.ToArray());
        workbook.Rows().Should().HaveCount(20_001);
    }

    [Fact]
    public async Task Cancellation_StopsTheWrite() {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => SpreadsheetWorkbook.WriteAsync(new MemoryStream(), [Anna], cancellationToken: cancellation.Token)
            .AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static string DecodeEscapes(string text) {
        return Regex.Replace(
            text,
            "_x([0-9A-Fa-f]{4})_",
            match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    }

    /// <summary>A write-only, non-seekable destination, like a response body.</summary>
    private sealed class ForwardOnlyStream : Stream {
        private readonly MemoryStream _inner = new();

        public long Written => Interlocked.Read(ref _written);

        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() {
            return _inner.ToArray();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            _inner.Write(buffer, offset, count);
            Interlocked.Add(ref _written, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            _inner.Write(buffer.Span);
            Interlocked.Add(ref _written, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() {
        }

        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }
    }
}
