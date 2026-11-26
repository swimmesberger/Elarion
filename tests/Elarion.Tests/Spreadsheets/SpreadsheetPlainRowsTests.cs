using AwesomeAssertions;
using Elarion.Spreadsheets;
using Xunit;
using static Elarion.Tests.Spreadsheets.HandBuiltWorkbook;

namespace Elarion.Tests.Spreadsheets;

/// <summary>The scenarios under Read a foreign layout as plain rows (FR-058, FR-059).</summary>
public sealed class SpreadsheetPlainRowsTests {
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static async Task<SpreadsheetRowsResult> ReadRowsAsync(
        byte[] bytes, SpreadsheetReadOptions? options = null, SpreadsheetWorksheetSelection? selection = null) {
        return await new SpreadsheetReader().ReadRowsAsync(new MemoryStream(bytes), options, selection, CancellationToken);
    }

    [Fact]
    public async Task BankStatementWithAccountDetailsAboveTheHeader_HoldsEveryNonEmptyRowWithItsTypedCells() {
        var bytes = Build(
            TextRow(1, "Account", "AT61 1904 3002 3457 3201") +
            TextRow(2, "Holder", "Anna") +
            TextRow(4, "Booking date", "Text", "Amount", "Booked", "Fee") +
            $"""<row r="5">{Number("A5", "46350", 2)}{Text("B5", "Rent")}{Number("C5", "-1234.5599999999999")}<c r="D5" t="b"><v>1</v></c><c r="E5" t="e"><v>#N/A</v></c></row>""" +
            $"""<row r="7">{Number("A7", "46351.5", 5)}<c r="C7"><v>12</v></c></row>""");

        var result = await ReadRowsAsync(bytes);

        result.FileErrors.Should().BeEmpty();
        result.Worksheet.Should().Be("Data");
        result.Rows.Select(row => row.RowNumber).Should().Equal(1, 2, 4, 5, 7);
        var booking = result.Rows[3];
        booking.Cells.Select(cell => cell.Kind).Should().Equal(
            SpreadsheetCellKind.Date, SpreadsheetCellKind.Text, SpreadsheetCellKind.Number, SpreadsheetCellKind.Boolean,
            SpreadsheetCellKind.Error);
        booking.Cells.Select(cell => cell.Text).Should().Equal("2026-11-24", "Rent", "-1234.56", "true", "#N/A");
        booking[2].TryGetNumber(out var amount).Should().BeTrue();
        amount.Should().Be(-1234.56m);
        booking[0].TryGetDate(out var date).Should().BeTrue();
        date.Should().Be(new DateTime(2026, 11, 24));
        booking[3].TryGetBoolean(out var booked).Should().BeTrue();
        booked.Should().BeTrue();
        var later = result.Rows[4];
        later.Cells.Select(cell => cell.Kind).Should().Equal(SpreadsheetCellKind.Date, SpreadsheetCellKind.Blank, SpreadsheetCellKind.Number);
        later[0].Text.Should().Be("2026-11-25T12:00:00");
        later[9].Kind.Should().Be(SpreadsheetCellKind.Blank);
    }

    [Fact]
    public async Task HiddenFirstWorksheet_IsSkipped() {
        var bytes = Build([new Sheet("Old", TextRow(1, "hidden"), "veryHidden"), new Sheet("Statement", TextRow(1, "visible"))]);

        var result = await ReadRowsAsync(bytes);

        result.Worksheet.Should().Be("Statement");
        result.Rows.Single()[0].Text.Should().Be("visible");
    }

    [Fact]
    public async Task MoreColumnsThanTheReadOptionsAllow_IsOneErrorNamingTheLimit() {
        var bytes = Build(TextRow(1, "a", "b") + TextRow(2, "a", "b", "c", "d"));

        var result = await ReadRowsAsync(bytes, new SpreadsheetReadOptions { MaxColumns = 3 });

        var error = result.FileErrors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.TooManyColumns);
        error.Limit.Should().Be(3);
        error.RowNumber.Should().Be(2);
        result.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task MoreRowsThanTheReadOptionsAllow_IsOneErrorNamingTheLimit() {
        var bytes = Build(TextRow(1, "a") + TextRow(2, "b") + TextRow(5, "c"));

        var result = await ReadRowsAsync(bytes, new SpreadsheetReadOptions { MaxRows = 2 });

        var error = result.FileErrors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.TooManyRows);
        error.Limit.Should().Be(2);
        result.Rows.Select(row => row.RowNumber).Should().Equal(1, 2);
    }

    [Fact]
    public async Task XlsSavedWithThe1904DateSystem_ReadsDatesAsTheSpreadsheetShowsThem() {
        var bytes = await File.ReadAllBytesAsync(SpreadsheetReaderOpenTests.FixturePath("date1904.xls"), CancellationToken);

        var result = await ReadRowsAsync(bytes, selection: new SpreadsheetWorksheetSelection { Name = "Data" });
        var values = result.Rows.Where(row => row.Cells.Count > 1).ToDictionary(row => row[0].Text, row => row[1]);

        result.FileErrors.Should().BeEmpty();
        values["date14"].Text.Should().Be("2030-11-25");
        values["datetime165"].Text.Should().Be("2030-11-25T10:30:00");
        values["time21"].Text.Should().Be("10:30:00");
        values["number"].Text.Should().Be("1234.56");
        values["binary1234.56"].Text.Should().Be("1234.56");
        values["16digits"].Text.Should().Be("1234567890123460");
        values["textfmt49 number"].Kind.Should().Be(SpreadsheetCellKind.Number);
        values["bool"].Text.Should().Be("true");
        values["error"].Text.Should().Be("#DIV/0!");
        values["formula num"].Text.Should().Be("2");
        values["ctrl"].Text.Should().Be("x\u0001y");
    }

    [Fact]
    public async Task NamingAWorksheetThatDoesNotExist_IsOneError() {
        var result = await new SpreadsheetReader().ReadRowsAsync(
            new MemoryStream(Build(TextRow(1, "a"))), selection: new SpreadsheetWorksheetSelection { Name = "Statement" },
            cancellationToken: CancellationToken);

        result.FileErrors.Should().ContainSingle().Which.Code.Should().Be(SpreadsheetErrorCodes.WorksheetNotFound);
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task OneDocument_ReadsRowsAndRecordsOfSeveralWorksheets() {
        var bytes = Build([
            new Sheet("Statement", TextRow(1, "Booking date") + TextRow(2, "x")),
            new Sheet("Contacts", TextRow(1, "Key", "Name") + TextRow(2, "1", "Anna"))
        ]);

        using var document = await new SpreadsheetReader().OpenAsync(new MemoryStream(bytes), cancellationToken: CancellationToken);
        var statement = document.ReadRows();
        var contacts = await document.ReadAsync<ContactUpdateRow>(new SpreadsheetWorksheetSelection { Name = "Contacts" }, CancellationToken);
        var statementAgain = document.ReadRows(new SpreadsheetWorksheetSelection { Name = "Statement" });

        statement.Rows.Should().HaveCount(2);
        contacts.Records.Single().Name.Should().Be("Anna");
        statementAgain.Rows.Should().HaveCount(2);
    }
}
