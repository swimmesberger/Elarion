using AwesomeAssertions;
using DocumentFormat.OpenXml.Spreadsheet;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

/// <summary>
/// Worksheets whose columns are known at run time, values that carry their own type, footer rows, column filters and
/// text columns with choices (ADR-0108, amendment 2026-11-27; FR-073 to FR-078).
/// </summary>
public sealed class SpreadsheetRuntimeSheetTests {
    private static readonly ContactRow Anna = new() {
        Name = "Anna",
        Phone = "+43 1 234",
        Email = "anna@example.com",
        Balance = 1234.5m,
        Kind = ContactKind.Organization
    };

    private static readonly SpreadsheetSheetDefinition ContactsSheet = new("Kontakte", [
        SpreadsheetColumnDefinition.Text("ID"),
        SpreadsheetColumnDefinition.Choice("E-Mail 1 Bezeichnung", ["Privat", "Arbeit"]),
        SpreadsheetColumnDefinition.Number("Fläche", format: "#,##0.00"),
        SpreadsheetColumnDefinition.Date("Geburtsdatum", format: "dd.mm.yyyy"),
        SpreadsheetColumnDefinition.Boolean("Aktiv"),
        SpreadsheetColumnDefinition.Text("Notiz") with { Width = 30 }
    ]);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static async Task<WrittenWorkbook> WriteAsync(Func<SpreadsheetWorkbook, CancellationToken, ValueTask> write) {
        var file = await SpreadsheetWorkbook.ToFileAsync("book.xlsx", write, cancellationToken: CancellationToken);
        file.ContentType.Should().Be(SpreadsheetWorkbook.ContentType);
        file.FileName.Should().Be("book.xlsx");
        return WrittenWorkbook.From(file.Bytes.ToArray());
    }

    [Fact]
    public async Task RuntimeSheet_WritesItsHeadersAndTypedValues_WithTheColumnsFormatsWidthsAndDropdowns() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(ContactsSheet, [
            ["K-1", "Arbeit", 1234.5m, new DateOnly(2026, 11, 24), true, "first"],
            ["K-2", null, 7]
        ], ct));

        workbook.WorksheetNames.Should().Equal("Kontakte");
        workbook.Rows()[0].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal(
            "ID", "E-Mail 1 Bezeichnung", "Fläche", "Geburtsdatum", "Aktiv", "Notiz");
        WrittenWorkbook.Text(workbook.Cell("A2")).Should().Be("K-1");
        workbook.Cell("C2").CellValue!.Text.Should().Be("1234.5");
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("C2")).NumberFormatId!.Value).Should().Be("#,##0.00");
        workbook.Cell("D2").CellValue!.Text.Should().Be("46350");
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("D2")).NumberFormatId!.Value).Should().Be("dd.mm.yyyy");
        workbook.Cell("E2").DataType!.Value.Should().Be(CellValues.Boolean);
        workbook.Cell("C3").CellValue!.Text.Should().Be("7");
        workbook.FindCell("F3").Should().NotBeNull("a short row ends with empty cells in the text format");
        workbook.Columns()[5].Width!.Value.Should().Be(30);
        workbook.DataValidation("B").Formula1!.Text.Should().Be("\"Privat,Arbeit\"");
    }

    [Fact]
    public async Task RuntimeSheet_ReadsBackAsPlainRows() {
        using var workbook = await WriteAsync(async (book, ct) => {
            await book.AddWorksheetAsync(ContactsSheet, Rows(), ct);
        });

        var result = new SpreadsheetReader().ReadRows(workbook.Bytes);

        result.FileErrors.Should().BeEmpty();
        result.Rows.Should().HaveCount(3);
        result.Rows[1][2].TryGetNumber(out var area).Should().BeTrue();
        area.Should().Be(10.25m);
        result.Rows[2][0].Text.Should().Be("K-2");

        static async IAsyncEnumerable<IReadOnlyList<object?>> Rows() {
            await Task.Yield();
            yield return ["K-1", "Privat", 10.25];
            yield return ["K-2"];
        }
    }

    [Fact]
    public async Task LabelInADateColumn_IsATextCell_AndASecondBlockKeepsEachValuesOwnType() {
        var sheet = new SpreadsheetSheetDefinition("Zeiten", [
            SpreadsheetColumnDefinition.Date("Datum", format: "dd.mm.yyyy"),
            SpreadsheetColumnDefinition.Number("Stunden", format: "0.00")
        ]);

        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(sheet, [
            [new DateOnly(2026, 11, 2), 7.5],
            ["Summe", 7.5],
            [],
            ["Zeitbuchungen"],
            [new DateOnly(2026, 11, 3), "Urlaub"],
            ["Saldo 2026 (h)", -3]
        ], ct));

        var total = workbook.Cell("A3");
        total.DataType!.Value.Should().Be(CellValues.InlineString);
        WrittenWorkbook.Text(total).Should().Be("Summe");
        workbook.CellFormat(total).NumberFormatId!.Value.Should().Be(49u, "a label takes the text format, not the date format");
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("A6")).NumberFormatId!.Value).Should().Be("dd.mm.yyyy");
        workbook.Cell("B6").DataType!.Value.Should().Be(CellValues.InlineString);
        workbook.Cell("B7").CellValue!.Text.Should().Be("-3");
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("B7")).NumberFormatId!.Value).Should().Be("0.00");
    }

    [Fact]
    public async Task RowWithMoreValuesThanColumns_FailsNamingTheRow() {
        var sheet = new SpreadsheetSheetDefinition("Data", [SpreadsheetColumnDefinition.Text("A")]);

        var write = () => WriteAsync((book, ct) => book.AddWorksheetAsync(sheet, [["a", "b"]], ct));

        (await write.Should().ThrowAsync<SpreadsheetWriteException>())
            .Which.RowNumber.Should().Be(2);
    }

    [Fact]
    public async Task ValueOfATypeNoCellHolds_FailsNamingTheCell() {
        var sheet = new SpreadsheetSheetDefinition("Data", [SpreadsheetColumnDefinition.Text("A")]);

        var write = () => WriteAsync((book, ct) => book.AddWorksheetAsync(sheet, [[new object()]], ct));

        (await write.Should().ThrowAsync<SpreadsheetWriteException>())
            .Which.CellReference.Should().Be("A2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a name longer than thirty-one chars")]
    public void DefinitionWithAnInvalidName_IsRefusedWhenBuilt(string name) {
        var create = () => new SpreadsheetSheetDefinition(name, []);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ColumnWidthOfZeroOrLess_IsRefused() {
        var create = () => SpreadsheetColumnDefinition.Text("A") with { Width = 0 };

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ExcludedColumns_LeaveTheColumnOut_AndTheOthersCloseUp() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            [Anna], new SpreadsheetWorksheetOptions { ExcludedColumns = [nameof(ContactRow.Email), nameof(ContactRow.Balance)] },
            ct));

        var headers = workbook.Rows()[0].Elements<Cell>().Select(WrittenWorkbook.Text).ToList();
        headers.Should().Equal(
            "Name", "Phone", "Customer since", "Visits", "Newsletter", "Kind", "Active", "NameLength");
        WrittenWorkbook.Text(workbook.Cell("B2")).Should().Be("+43 1 234");
        workbook.PartXml("xl/worksheets/sheet1.xml").Should().NotContain("anna@example.com");
        // The Kind dropdown moved with its column, from H to F.
        workbook.DataValidation("F").Formula1!.Text.Should().Contain("Company");
    }

    [Fact]
    public async Task IncludedColumns_WriteOnlyThose_InTheRecordsOrder() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            [Anna], new SpreadsheetWorksheetOptions { IncludedColumns = [nameof(ContactRow.Kind), nameof(ContactRow.Name)] }, ct));

        workbook.Rows()[0].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal("Name", "Kind");
        workbook.Rows()[1].Elements<Cell>().Select(WrittenWorkbook.Text).Should().Equal("Anna", "Company");
    }

    [Fact]
    public async Task ColumnFilterNamingNoColumn_ThrowsBeforeWriting_AndTheWorkbookStaysUsable() {
        using var stream = new MemoryStream();
        await using var book = SpreadsheetWorkbook.Create(stream);

        var add = () => book.AddWorksheetAsync(
            [Anna], new SpreadsheetWorksheetOptions { ExcludedColumns = ["Mail"] }, CancellationToken).AsTask();

        (await add.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("'Mail'");
        await book.AddWorksheetAsync([Anna], cancellationToken: CancellationToken);
    }

    [Fact]
    public async Task FooterRows_FollowTheRecords_WithAnyTypeInAnyColumn_AndTheDropdownsStopAboveThem() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            [Anna, Anna], new SpreadsheetWorksheetOptions {
                IncludedColumns = [nameof(ContactRow.CustomerSince), nameof(ContactRow.Balance), nameof(ContactRow.Kind)],
                FooterRows = [["Total", 2469m], [], ["Printed", new DateOnly(2026, 11, 27)]]
            }, ct));

        var rows = workbook.Rows();
        rows.Select(row => row.RowIndex!.Value).Should().Equal(1u, 2u, 3u, 4u, 5u, 6u);
        WrittenWorkbook.Text(workbook.Cell("A4")).Should().Be("Total");
        workbook.CellFormat(workbook.Cell("A4")).NumberFormatId!.Value.Should().Be(49u);
        workbook.CustomFormatCode(workbook.CellFormat(workbook.Cell("B4")).NumberFormatId!.Value).Should().Be("#,##0.00");
        workbook.Cell("B6").CellValue!.Text.Should().Be("46353");
        workbook.DataValidation("C").SequenceOfReferences!.InnerText.Should().Be("C2:C3");
    }

    [Fact]
    public async Task FooterRowWithMoreValuesThanTheWrittenColumns_Fails() {
        var write = () => WriteAsync((book, ct) => book.AddWorksheetAsync(
            [Anna], new SpreadsheetWorksheetOptions {
                IncludedColumns = [nameof(ContactRow.Name)],
                FooterRows = [["Total", 1]]
            }, ct));

        (await write.Should().ThrowAsync<SpreadsheetWriteException>()).Which.RowNumber.Should().Be(3);
    }

    [Fact]
    public async Task TextColumnWithChoices_OffersTheListAsADropdown() {
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(
            [new ChannelRow { Name = "Anna", Label = "Work", Platform = "Mail" }], cancellationToken: ct));

        ChannelRow.Columns.Label.Kind.Should().Be(SpreadsheetValueKind.Choice);
        ChannelRow.Columns.Label.Choices.Should().Equal("Private", "Work");
        workbook.DataValidation("B").Formula1!.Text.Should().Be("\"Private,Work\"");
        workbook.DataValidation("C").Formula1!.Text.Should().Be("\"Website,Mail,Phone\"");
        WrittenWorkbook.Text(workbook.Cell("B2")).Should().Be("Work");
    }

    [Fact]
    public async Task TextColumnWithChoices_ReadsAnyText_ButAStrictOneOnlyItsLabels() {
        var sheet = new SpreadsheetSheetDefinition("Channels", [
            SpreadsheetColumnDefinition.Text("Name"),
            SpreadsheetColumnDefinition.Text("Label"),
            SpreadsheetColumnDefinition.Text("Platform")
        ]);
        using var workbook = await WriteAsync((book, ct) => book.AddWorksheetAsync(sheet, [
            ["Anna", "Holiday home", " mail "],
            ["Bert", "Work", "Fax"]
        ], ct));

        var result = await new SpreadsheetReader().ReadAsync<ChannelRow>(
            new MemoryStream(workbook.Bytes), cancellationToken: CancellationToken);

        result.Rows[0].Record!.Label.Should().Be("Holiday home", "a column without StrictChoices reads any text");
        result.Rows[0].Record!.Platform.Should().Be("Mail", "a strict column returns the label as it is spelled");
        var error = result.Rows[1].Errors.Should().ContainSingle().Which;
        error.Code.Should().Be(SpreadsheetErrorCodes.InvalidValue);
        error.CellReference.Should().Be("C3");
        error.AllowedValues.Should().Equal("Website", "Mail", "Phone");
    }

    [Fact]
    public async Task PlainRowsFromMemory_ReadSynchronously_FromAnOffsetIntoABuffer() {
        var bytes = await SpreadsheetReaderRecordTests.WriteAsync([Anna]);
        var padded = new byte[bytes.Length + 10];
        bytes.CopyTo(padded, 7);

        var result = new SpreadsheetReader().ReadRows(padded.AsMemory(7, bytes.Length));

        result.FileErrors.Should().BeEmpty();
        result.Rows.Should().HaveCount(2);
        result.Rows[1][0].Text.Should().Be("Anna");
    }

    [Fact]
    public void PlainRowsFromMemoryLargerThanTheLimit_IsTooLarge() {
        var result = new SpreadsheetReader().ReadRows(new byte[100], new SpreadsheetReadOptions { MaxUnpackedBytes = 50 });

        result.FileErrors.Should().ContainSingle().Which.Code.Should().Be(SpreadsheetErrorCodes.TooLarge);
    }

    [Fact]
    public async Task PlainRowsFromMemory_CountTheHeaderRowAgainstMaxRows() {
        var bytes = await SpreadsheetReaderRecordTests.WriteAsync([Anna, Anna]);

        var result = new SpreadsheetReader().ReadRows(bytes, new SpreadsheetReadOptions { MaxRows = 2 });

        result.Rows.Should().HaveCount(2, "the header row and the first data row");
        result.FileErrors.Should().ContainSingle().Which.Code.Should().Be(SpreadsheetErrorCodes.TooManyRows);
    }

    [Fact]
    public async Task NumberAndDateCells_CarryTheirNumberFormat_OtherCellsNone() {
        var sheet = new SpreadsheetSheetDefinition("Data", [
            SpreadsheetColumnDefinition.Number("Amount", format: "#,##0.00 \"€\""),
            SpreadsheetColumnDefinition.Date("On", format: "dd.mm.yyyy"),
            SpreadsheetColumnDefinition.Text("Note")
        ]);
        using var workbook = await WriteAsync((book, ct) =>
            book.AddWorksheetAsync(sheet, [[1234.5m, new DateOnly(2026, 11, 24), "x"]], ct));

        var row = new SpreadsheetReader().ReadRows(workbook.Bytes).Rows[1];

        row[0].NumberFormat.Should().Be("#,##0.00 \"€\"");
        row[1].Kind.Should().Be(SpreadsheetCellKind.Date);
        row[1].NumberFormat.Should().Be("dd.mm.yyyy");
        row[2].NumberFormat.Should().BeNull();
        row[0].Should().Be(SpreadsheetCell.FromNumber(1234.5), "the format takes no part in equality");
    }
}
