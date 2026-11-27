using AwesomeAssertions;
using DocumentFormat.OpenXml.Spreadsheet;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

/// <summary>
/// General columns, title rows, hidden worksheets, worksheet options for run-time worksheets and the one-line file, and
/// the synchronous write into memory (ADR-0108, amendment 2026-11-27, second round; FR-081 to FR-086).
/// </summary>
public sealed class SpreadsheetWorksheetLayoutTests {
    private static readonly ContactRow Anna = new() { Name = "Anna", Balance = 1234.5m, Kind = ContactKind.Organization };
    private static readonly ContactRow Bert = new() { Name = "Bert", Balance = -0.5m, Kind = ContactKind.Authority };

    private static readonly SpreadsheetSheetDefinition Bookings = new("Buchungen", [
        SpreadsheetColumnDefinition.Date("Datum", format: "dd.mm.yyyy"),
        SpreadsheetColumnDefinition.Choice("Art", ["Arbeit", "Urlaub"]),
        SpreadsheetColumnDefinition.Number("Stunden", format: "0.00")
    ]);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static async Task<WrittenWorkbook> WriteAsync(Func<SpreadsheetWorkbook, CancellationToken, ValueTask> write) {
        var file = await SpreadsheetWorkbook.ToFileAsync("book.xlsx", write, cancellationToken: CancellationToken);
        return WrittenWorkbook.From(file.Bytes.ToArray());
    }

    [Fact]
    public async Task GeneralColumn_WritesTextAndNumbersAsTheirOwnTypes_WithoutAColumnFormat() {
        var sheet = new SpreadsheetSheetDefinition("Data", [
            SpreadsheetColumnDefinition.Text("Name"),
            SpreadsheetColumnDefinition.General("Reference")
        ]);

        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(sheet, [
            ["a", 4711],
            ["b", "R-12"],
            ["c", 12.5m],
            ["d", new DateOnly(2026, 11, 27)],
            ["e"]
        ], ct));

        SpreadsheetColumnDefinition.General("Reference").Kind.Should().Be(SpreadsheetValueKind.General);
        workbook.Columns()[1].Style.Should().BeNull("a general column has no format of its own, so typed cells stay General");
        workbook.Cell("B2").CellValue!.Text.Should().Be("4711");
        workbook.Cell("B2").StyleIndex.Should().BeNull();
        workbook.Cell("B3").DataType!.Value.Should().Be(CellValues.InlineString);
        WrittenWorkbook.Text(workbook.Cell("B3")).Should().Be("R-12");
        workbook.Cell("B3").StyleIndex.Should().BeNull("text in a general column keeps the General format, not \"@\"");
        workbook.Cell("B4").CellValue!.Text.Should().Be("12.5");
        workbook.CellFormat(workbook.Cell("B5")).NumberFormatId!.Value.Should().Be(14u, "a date still shows as a date");
        workbook.FindCell("B6").Should().BeNull("an empty value in a general column needs no cell");

        // Read back: each cell keeps its own type.
        var rows = new SpreadsheetReader().ReadRows(workbook.Bytes).Rows;
        rows[1][1].TryGetNumber(out var number).Should().BeTrue();
        number.Should().Be(4711m);
        rows[2][1].Text.Should().Be("R-12");
    }

    [Fact]
    public async Task RuntimeSheet_TakesWorksheetOptions_FooterRowsEndTheDropdowns_AndTheNameCanChange() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(Bookings, [
            [new DateOnly(2026, 11, 2), "Arbeit", 7.5],
            [new DateOnly(2026, 11, 3), "Urlaub", 7.5]
        ], new SpreadsheetWorksheetOptions { Name = "November", FooterRows = [["Summe", null, 15m]] }, ct));

        workbook.WorksheetNames.Should().Equal("November");
        WrittenWorkbook.Text(workbook.Cell("A4")).Should().Be("Summe");
        workbook.CellFormat(workbook.Cell("A4")).NumberFormatId!.Value.Should().Be(49u);
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("C4")).NumberFormatId!.Value).Should().Be("0.00");
        workbook.DataValidation("B").SequenceOfReferences!.InnerText.Should().Be("B2:B3", "the total row gets no dropdown");
    }

    [Fact]
    public async Task RuntimeSheet_FromAsyncRows_TakesWorksheetOptions() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            Bookings, Rows(), new SpreadsheetWorksheetOptions { FooterRows = [["Summe"]] }, ct));

        workbook.Rows().Should().HaveCount(3);
        workbook.DataValidation("B").SequenceOfReferences!.InnerText.Should().Be("B2:B2");

        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows() {
            await Task.Yield();
            yield return [new DateOnly(2026, 11, 2), "Arbeit", 7.5];
        }
    }

    [Fact]
    public async Task RuntimeSheet_WithAColumnFilter_IsRefused_AndTheWorkbookStaysUsable() {
        using var stream = new MemoryStream();
        await using var book = SpreadsheetWorkbook.Create(stream);

        var add = () => book.AddWorksheetAsync(
            Bookings, [], new SpreadsheetWorksheetOptions { ExcludedColumns = ["Art"] }, CancellationToken).AsTask();

        (await add.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("column filter");
        await book.AddWorksheetAsync(Bookings, [], CancellationToken);
    }

    [Fact]
    public async Task TitleRows_StandAboveTheHeader_WhichStaysFrozen_AndTheDropdownsStartBelowIt() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(Bookings, [
            [new DateOnly(2026, 11, 2), "Arbeit", 7.5]
        ], new SpreadsheetWorksheetOptions {
            TitleRows = [["Zeitbuchungen November 2026"], [], ["Mitarbeiter", "Anna"]]
        }, ct));

        WrittenWorkbook.Text(workbook.Cell("A1")).Should().Be("Zeitbuchungen November 2026");
        WrittenWorkbook.Text(workbook.Cell("B3")).Should().Be("Anna");
        workbook.Rows().Select(row => row.RowIndex!.Value).Should().Equal(1u, 2u, 3u, 4u, 5u);
        workbook.Rows()[3].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal("Datum", "Art", "Stunden");
        workbook.CellFormat(workbook.Cell("A4")).FontId!.Value.Should().Be(1u, "the header row keeps its bold style");
        workbook.Cell("A5").CellValue!.Text.Should().Be("46328");

        var pane = workbook.Worksheet().Descendants<Pane>().Single();
        pane.VerticalSplit!.Value.Should().Be(4);
        pane.TopLeftCell!.Value.Should().Be("A5");
        workbook.DataValidations().Single().SequenceOfReferences!.InnerText.Should().Be("B5:B2004");
    }

    [Fact]
    public async Task TitleRowsOnARecordWorksheet_ReadBackWithTheHeaderRowNamed() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            [Anna, Bert], new SpreadsheetWorksheetOptions { TitleRows = [["Kontakte, Stand 27.11.2026"]] }, ct));

        var result = await SpreadsheetReaderRecordTests.ReadAsync<ContactRow>(
            workbook.Bytes, selection: new SpreadsheetWorksheetSelection { HeaderRow = 2 });

        result.HasErrors.Should().BeFalse();
        result.Records.Select(contact => contact.Name).Should().Equal("Anna", "Bert");
        result.Rows[0].RowNumber.Should().Be(3);
    }

    [Fact]
    public async Task ARecordErrorBelowTitleRows_NamesTheRowTheSpreadsheetShows() {
        var sheet = new SpreadsheetSheetDefinition("Data", [SpreadsheetColumnDefinition.Text("A")]);

        var write = () => WriteAsync((book, ct) => book.AddWorksheetAsync(
            sheet, [["ok"], [new object()]], new SpreadsheetWorksheetOptions { TitleRows = [["Title"], ["Subtitle"]] }, ct));

        (await write.Should().ThrowAsync<SpreadsheetWriteException>()).Which.CellReference.Should().Be("A5");
    }

    [Fact]
    public async Task HiddenWorksheet_IsWrittenHidden_TheWorkbookOpensOnTheFirstVisible_AndAReaderSkipsIt() {
        using var workbook = await WriteAsync(async (book, ct) => {
            await book.AddWorksheetAsync(Bookings, [], new SpreadsheetWorksheetOptions { Name = "Hilfswerte", Hidden = true }, ct);
            await book.AddWorksheetAsync([Anna], cancellationToken: ct);
        });

        workbook.Sheet(0).State!.Value.Should().Be(SheetStateValues.Hidden);
        workbook.Sheet(1).State.Should().BeNull();
        workbook.WorkbookPart.Workbook!.BookViews!.Elements<WorkbookView>().Single().ActiveTab!.Value.Should().Be(1u);
        workbook.Worksheet(0).Descendants<SheetView>().Single().TabSelected.Should().BeNull();
        workbook.Worksheet(1).Descendants<SheetView>().Single().TabSelected!.Value.Should().BeTrue();

        var result = await SpreadsheetReaderRecordTests.ReadAsync<ContactRow>(workbook.Bytes);
        result.Records.Should().ContainSingle().Which.Name.Should().Be("Anna");
    }

    [Fact]
    public async Task WorkbookWithoutHiddenWorksheets_HasNoBookViews() {
        using var workbook = WrittenWorkbook.From(await SpreadsheetReaderRecordTests.WriteAsync([Anna]));

        workbook.PartXml("xl/workbook.xml").Should().NotContain("bookViews");
        workbook.PartXml("xl/worksheets/sheet1.xml").Should().Contain(
            """<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>""");
    }

    [Fact]
    public async Task WorkbookWhoseWorksheetsAreAllHidden_FailsWhenDisposed() {
        using var stream = new MemoryStream();
        var book = SpreadsheetWorkbook.Create(stream);
        await book.AddWorksheetAsync([Anna], new SpreadsheetWorksheetOptions { Hidden = true }, CancellationToken);

        var dispose = () => book.DisposeAsync().AsTask();

        (await dispose.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("not hidden");
    }

    [Fact]
    public async Task OneLineFile_TakesWorksheetOptions() {
        var file = await SpreadsheetWorkbook.ToFileAsync(
            [Anna, Bert], "contacts.xlsx",
            new SpreadsheetWorksheetOptions {
                IncludedColumns = [nameof(ContactRow.Name), nameof(ContactRow.Kind), nameof(ContactRow.Balance)],
                FooterRows = [["Total", 1234m]]
            },
            cancellationToken: CancellationToken);

        file.FileName.Should().Be("contacts.xlsx");
        file.ContentType.Should().Be(SpreadsheetWorkbook.ContentType);
        using var workbook = WrittenWorkbook.From(file.Bytes.ToArray());
        workbook.Rows()[0].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal("Name", "Balance", "Kind");
        WrittenWorkbook.Text(workbook.Cell("A4")).Should().Be("Total");
        workbook.DataValidation("C").SequenceOfReferences!.InnerText.Should().Be("C2:C3");
    }

    [Fact]
    public async Task OneLineFileFromAsyncRecords_TakesWorksheetOptions() {
        var file = await SpreadsheetWorkbook.ToFileAsync(
            Records(), "contacts.xlsx", new SpreadsheetWorksheetOptions { Name = "People" },
            cancellationToken: CancellationToken);

        using var workbook = WrittenWorkbook.From(file.Bytes.ToArray());
        workbook.WorksheetNames.Should().Equal("People");

        static async IAsyncEnumerable<ContactRow> Records() {
            await Task.Yield();
            yield return Anna;
        }
    }

    [Fact]
    public async Task ToBytes_WritesTheSameWorkbookAsTheAsynchronousWrite() {
        var options = new SpreadsheetWorksheetOptions { FooterRows = [["Total"]] };
        var expected = await SpreadsheetWorkbook.ToFileAsync([Anna, Bert], "c.xlsx", options, cancellationToken: CancellationToken);

        var bytes = SpreadsheetWorkbook.ToBytes([Anna, Bert], options);

        bytes.Should().Equal(expected.Bytes.ToArray());
        var result = await SpreadsheetReaderRecordTests.ReadAsync<ContactRow>(bytes);
        result.Rows.Take(2).Select(row => row.Record!.Name).Should().Equal("Anna", "Bert");
    }

    [Fact]
    public async Task ToBytes_OfManyRecords_CompletesSynchronously_AcrossSeveralFlushes() {
        // 20,000 rows are several megabytes of worksheet XML: many buffer flushes into the zip entry, each of which
        // would fail the synchronous write if it had to wait.
        var records = Enumerable.Range(0, 20_000).Select(i => new ContactRow { Name = $"Contact {i}", Balance = i }).ToList();

        var bytes = SpreadsheetWorkbook.ToBytes(records);

        var result = await SpreadsheetReaderRecordTests.ReadAsync<ContactRow>(bytes);
        result.Records.Should().HaveCount(20_000);
        result.Records.Last().Name.Should().Be("Contact 19999");
    }

    [Fact]
    public void ToBytes_OfARuntimeSheet_ReadsBack() {
        var bytes = SpreadsheetWorkbook.ToBytes(Bookings, [[new DateOnly(2026, 11, 2), "Arbeit", 7.5]],
            new SpreadsheetWorksheetOptions { TitleRows = [["November"]] });

        using var workbook = WrittenWorkbook.From(bytes);
        var rows = new SpreadsheetReader().ReadRows(bytes).Rows;
        rows.Should().HaveCount(3);
        rows[0][0].Text.Should().Be("November");
        rows[1][1].Text.Should().Be("Art");
        rows[2][2].TryGetNumber(out var hours).Should().BeTrue();
        hours.Should().Be(7.5m);
    }

    [Fact]
    public void ToBytes_WhenAValueCannotBeWritten_ThrowsTheWriteError() {
        var sheet = new SpreadsheetSheetDefinition("Data", [SpreadsheetColumnDefinition.Text("A")]);

        var write = () => SpreadsheetWorkbook.ToBytes(sheet, [[new object()]]);

        write.Should().Throw<SpreadsheetWriteException>().Which.CellReference.Should().Be("A2");
    }
}
