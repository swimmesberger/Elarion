using System.Globalization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Spreadsheets;
using Xunit;
using static Elarion.Tests.Spreadsheets.HandBuiltWorkbook;

namespace Elarion.Tests.Spreadsheets;

/// <summary>Reading records: the scenarios under Import, Preview, Update only what the file states, Report problems and Several worksheets.</summary>
public sealed class SpreadsheetReaderRecordTests {
    private static readonly ContactRow Anna = new() {
        Name = "Anna",
        Phone = "+43 1 234",
        Email = "anna@example.com",
        CustomerSince = new DateOnly(2026, 11, 24),
        Balance = 1234.56m,
        Visits = 7,
        Newsletter = true,
        Kind = ContactKind.Organization,
        Active = false
    };

    private static readonly ContactRow Bert = new() { Name = "Bert", Kind = ContactKind.Authority, Active = true, Balance = -0.5m };

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    internal static async Task<byte[]> WriteAsync<T>(IEnumerable<T> records) where T : class, ISpreadsheetRecord<T> {
        using var stream = new MemoryStream();
        await SpreadsheetWorkbook.WriteAsync(stream, records, cancellationToken: CancellationToken);
        return stream.ToArray();
    }

    internal static async Task<SpreadsheetReadResult<T>> ReadAsync<T>(
        byte[] bytes, SpreadsheetReadOptions? options = null, SpreadsheetWorksheetSelection? selection = null)
        where T : class, ISpreadsheetRecord<T> {
        return await new SpreadsheetReader().ReadAsync<T>(new MemoryStream(bytes), options, selection, CancellationToken);
    }

    private static void ShouldEqual(ContactRow actual, ContactRow expected) {
        actual.Should().BeEquivalentTo(expected, options => options.Excluding(row => row.InternalNote));
    }

    [Fact]
    public async Task WrittenRecords_ReadBackEqual() {
        var result = await ReadAsync<ContactRow>(await WriteAsync([Anna, Bert]));

        result.HasErrors.Should().BeFalse();
        result.Worksheet.Should().Be("Contacts");
        result.Rows.Select(row => row.RowNumber).Should().Equal(2, 3);
        var records = result.Records.ToList();
        ShouldEqual(records[0], Anna);
        ShouldEqual(records[1], Bert);
    }

    [Fact]
    public async Task EverySupportedType_WrittenAndReadBack_IsEqual() {
        var value = new ValueRow {
            Text = "x\u0001y _x0041_ \t tab",
            Amount = 0.1 + 0.2,
            Ratio = 1.5f,
            Count = -999_999_999_999_999,
            Big = 123_456_789_012_345,
            Id = Guid.CreateVersion7(),
            Missing = null
        };

        var result = await ReadAsync<ValueRow>(await WriteAsync([value]));

        result.HasErrors.Should().BeFalse(string.Join("; ", result.Errors));
        var read = result.Records.Single();
        read.Should().BeEquivalentTo(value, options => options.Excluding(row => row.Amount));
        // A number cell holds the 15 significant digits a spreadsheet shows (SC-002).
        read.Amount.Should().Be(0.3);
    }

    [Fact]
    public async Task HeadersInAnotherOrderCaseAndSpacing_WithAnExtraColumn_ReadEveryRow() {
        var bytes = Build(
            TextRow(1, "  e-MAIL ", "Unrelated", "KIND", "name", "balance", "Visits", "Newsletter", "Active") +
            TextRow(2, "anna@example.com", "ignored", "Company", "Anna", "1234.56", "7", "yes", "Nein"));

        var result = await ReadAsync<ContactRow>(bytes);

        result.HasErrors.Should().BeFalse(string.Join("; ", result.Errors));
        var anna = result.Records.Single();
        anna.Name.Should().Be("Anna");
        anna.Email.Should().Be("anna@example.com");
        anna.Kind.Should().Be(ContactKind.Organization);
        anna.Balance.Should().Be(1234.56m);
        anna.Visits.Should().Be(7);
        anna.Newsletter.Should().BeTrue();
        anna.Active.Should().BeFalse();
        anna.Phone.Should().BeNull();
    }

    [Fact]
    public async Task RenamedColumn_ReadsFromItsAlias_AndTheOwnHeaderWinsOverTheAlias() {
        var aliasOnly = Build(TextRow(1, "Name", "Mail", "Balance", "Visits", "Newsletter", "Kind", "Active") +
                              TextRow(2, "Anna", "old@example.com", "1", "1", "no", "Company", "Ja"));
        var both = Build(TextRow(1, "Name", "Mail", "E-mail", "Balance", "Visits", "Newsletter", "Kind", "Active") +
                         TextRow(2, "Anna", "old@example.com", "new@example.com", "1", "1", "no", "Company", "Ja"));

        var fromAlias = await ReadAsync<ContactRow>(aliasOnly);
        var fromBoth = await ReadAsync<ContactRow>(both);

        fromAlias.Records.Single().Email.Should().Be("old@example.com");
        fromAlias.Contains(ContactRow.Columns.Email).Should().BeTrue();
        fromBoth.HasErrors.Should().BeFalse();
        fromBoth.Records.Single().Email.Should().Be("new@example.com");
    }

    [Fact]
    public async Task SameDataSavedAsXls_ReadsTheSameRecordsAsXlsx() {
        // Fixtures/contacts.xls is contacts.xlsx (WriteXlsFixtureSource) saved as .xls by LibreOffice.
        var xls = await File.ReadAllBytesAsync(SpreadsheetReaderOpenTests.FixturePath("contacts.xls"), CancellationToken);

        var fromXls = await ReadAsync<ContactRow>(xls);
        var fromXlsx = await ReadAsync<ContactRow>(await WriteAsync([Anna, Bert]));

        fromXls.HasErrors.Should().BeFalse(string.Join("; ", fromXls.Errors));
        fromXls.Records.Should().BeEquivalentTo(fromXlsx.Records, options => options.WithStrictOrdering());
    }

    [Fact(Explicit = true)]
    public async Task WriteXlsFixtureSource() {
        // Run explicitly, then: soffice --headless --convert-to xls contacts.xlsx
        await File.WriteAllBytesAsync(Path.Combine(Path.GetTempPath(), "contacts.xlsx"), await WriteAsync([Anna, Bert]), CancellationToken);
    }

    [Fact]
    public async Task FirstWorksheetHidden_TheFirstVisibleWorksheetIsRead() {
        var bytes = Build([
            new Sheet("Old", TextRow(1, "Key", "Name") + TextRow(2, "0", "Hidden"), "hidden"),
            new Sheet("Contacts", TextRow(1, "Key", "Name") + TextRow(2, "1", "Visible"))
        ]);

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        result.Worksheet.Should().Be("Contacts");
        result.Records.Single().Name.Should().Be("Visible");
    }

    [Fact]
    public async Task TitleRowAboveTheHeaders_NamingTheHeaderRowReadsBelowIt_OtherwiseTheFirstNonEmptyRowIsTheHeader() {
        var bytes = Build(TextRow(3, "Contact export", "", "") + TextRow(4, "Key", "Name", "Phone") + TextRow(5, "1", "Anna", "123"));

        var named = await ReadAsync<ContactUpdateRow>(bytes, selection: new SpreadsheetWorksheetSelection { HeaderRow = 4 });
        var unnamed = await ReadAsync<ContactUpdateRow>(bytes);

        named.HasErrors.Should().BeFalse();
        named.Rows.Single().RowNumber.Should().Be(5);
        named.Records.Single().Phone.Should().Be("123");
        unnamed.FileErrors.Select(error => error.Header).Should().BeEquivalentTo("Key", "Name");
    }

    [Fact]
    public async Task EmptyRowsBetweenAndAfterTheData_ProduceNoRowsAndNoErrors_AndRowsKeepTheirNumbers() {
        var bytes = Build(
            TextRow(1, "Key", "Name") + TextRow(2, "1", "Anna") + TextRow(3, " ", "") + TextRow(6, "2", "Bert") +
            """<row r="7"><c r="A7" s="4"/></row>""" + TextRow(9, "", ""));

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        result.HasErrors.Should().BeFalse();
        result.Rows.Select(row => row.RowNumber).Should().Equal(2, 6);
    }

    [Fact]
    public async Task FormulaCells_ReadAsTheirLastCalculatedValue() {
        var bytes = Build(
            TextRow(1, "Text", "Amount", "Flag") +
            """<row r="2"><c r="A2" t="str"><f>"a"&amp;"b"</f><v>ab</v></c><c r="B2"><f>1+1</f><v>2</v></c><c r="C2" t="b"><f>TRUE()</f><v>1</v></c></row>""");

        var row = (await ReadAsync<LenientRow>(bytes)).Records.Single();

        row.Text.Should().Be("ab");
        row.Amount.Should().Be(2m);
        row.Flag.Should().BeTrue();
    }

    [Fact]
    public async Task Workbook1904_DatesReadAsTheSpreadsheetShowsThem() {
        // 46350 is 2026-11-24 in the 1900 system and 2030-11-25 in the 1904 system.
        var rows = TextRow(1, "On", "At", "Time") + $"<row r=\"2\">{Number("A2", "46350", 1)}{Number("B2", "46350.4375", 5)}{Number("C2", "0.4375", 3)}</row>";

        var system1900 = (await ReadAsync<LenientRow>(Build(rows))).Records.Single();
        var system1904 = (await ReadAsync<LenientRow>(Build(rows, date1904: true))).Records.Single();

        system1900.On.Should().Be(new DateOnly(2026, 11, 24));
        system1900.At.Should().Be(new DateTime(2026, 11, 24, 10, 30, 0));
        system1904.On.Should().Be(new DateOnly(2030, 11, 25));
        system1904.At.Should().Be(new DateTime(2030, 11, 25, 10, 30, 0));
        system1900.Time.Should().Be(new TimeOnly(10, 30));
        system1904.Time.Should().Be(new TimeOnly(10, 30));
    }

    [Fact]
    public async Task OneWorkbookWithTwoWorksheets_ReadsEachAsItsRecordType_AfterOpeningOnce() {
        using var stream = new MemoryStream();
        await using (var workbook = SpreadsheetWorkbook.Create(stream)) {
            await workbook.AddWorksheetAsync([Anna], cancellationToken: CancellationToken);
            await workbook.AddWorksheetAsync([new BankAccountRow { Iban = "AT61", Holder = "Anna" }], cancellationToken: CancellationToken);
        }

        using var document = await new SpreadsheetReader().OpenAsync(new MemoryStream(stream.ToArray()), cancellationToken: CancellationToken);
        var accounts = await document.ReadAsync<BankAccountRow>(new SpreadsheetWorksheetSelection { Name = "bank ACCOUNTS" }, CancellationToken);
        var contacts = await document.ReadAsync<ContactRow>(new SpreadsheetWorksheetSelection { Name = "Contacts" }, CancellationToken);
        var again = await document.ReadAsync<BankAccountRow>(new SpreadsheetWorksheetSelection { Name = "Bank accounts" }, CancellationToken);

        accounts.Worksheet.Should().Be("Bank accounts");
        accounts.Records.Single().Iban.Should().Be("AT61");
        ShouldEqual(contacts.Records.Single(), Anna);
        again.Records.Single().Holder.Should().Be("Anna");
    }

    [Fact]
    public async Task NamingTheSecondWorksheet_ReadsIt_AndANameThatDoesNotExistIsOneError() {
        var bytes = Build([
            new Sheet("First", TextRow(1, "Key", "Name") + TextRow(2, "1", "First")),
            new Sheet("Second", TextRow(1, "Key", "Name") + TextRow(2, "2", "Second"))
        ]);

        var second = await ReadAsync<ContactUpdateRow>(bytes, selection: new SpreadsheetWorksheetSelection { Name = "Second" });
        var missing = await ReadAsync<ContactUpdateRow>(bytes, selection: new SpreadsheetWorksheetSelection { Name = "Third" });

        second.Records.Single().Name.Should().Be("Second");
        var error = missing.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.WorksheetNotFound);
        error.Worksheet.Should().Be("Third");
        missing.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task EnumColumnWithLabelsMemberNamesAndOtherCase_ReadsEachAsItsMember() {
        var bytes = Build(TextRow(1, "Kind") + TextRow(2, "Private person") + TextRow(3, "Person") + TextRow(4, " company ") +
                          TextRow(5, "AUTHORITY") + TextRow(6, "organization"));

        var kinds = (await ReadAsync<LenientRow>(bytes)).Records.Select(row => row.Kind);

        kinds.Should().Equal(ContactKind.Person, ContactKind.Person, ContactKind.Organization, ContactKind.Authority, ContactKind.Organization);
    }

    [Fact]
    public async Task EnumMembersWithAliases_ReadFromEachAlias_AndAreWrittenAndOfferedOnlyAsTheirLabels() {
        var bytes = Build(TextRow(1, "Source") + TextRow(2, "gas") + TextRow(3, "GAS ") + TextRow(4, "Erdgas") +
                          TextRow(5, "waerme"));

        var sources = (await ReadAsync<MeterRow>(bytes)).Records.Select(row => row.Source);

        sources.Should().Equal(EnergySource.NaturalGas, EnergySource.NaturalGas, EnergySource.NaturalGas, EnergySource.Heating);

        using var workbook = await WrittenWorkbook.WriteAsync(
            new[] { new MeterRow { Source = EnergySource.NaturalGas }, new MeterRow { Source = EnergySource.Heating } });

        WrittenWorkbook.Text(workbook.Cell("A2")).Should().Be("Erdgas");
        WrittenWorkbook.Text(workbook.Cell("A3")).Should().Be("Wärme");
        workbook.DataValidation("A").Formula1!.Text.Should().Be("\"Erdgas,Wärme\"");
    }

    [Fact]
    public async Task FortyRowsOfWhichThreeHaveProblems_HoldEveryRowWithItsCellTexts_RecordsOrErrors() {
        var rows = TextRow(1, "Name", "Balance", "Visits", "Newsletter", "Kind", "Active");
        for (var r = 2; r <= 41; r++)
            rows += TextRow(r, $"Person {r}", r is 13 or 27 ? "lots" : "1,5", r == 40 ? "many" : r.ToString(CultureInfo.InvariantCulture), "x", "Company", "Ja");

        var result = await ReadAsync<ContactRow>(Build(rows));

        result.Rows.Should().HaveCount(40);
        result.Rows.Count(row => row.IsValid).Should().Be(37);
        result.Records.Should().HaveCount(37);
        var broken = result.Rows.Where(row => !row.IsValid).ToList();
        broken.Select(row => row.RowNumber).Should().Equal(13, 27, 40);
        broken.Should().OnlyContain(row => row.Record == null && row.Errors.Count == 1);
        broken[0].CellText(ContactRow.Columns.Balance).Should().Be("lots");
        result.Rows[0].CellText(ContactRow.Columns.Name).Should().Be("Person 2");
        result.Rows[0].CellText(ContactRow.Columns.Phone).Should().BeEmpty("the file lacks the column");
    }

    [Fact]
    public async Task WorkbookWith500BrokenRows_ReportsEveryError() {
        var rows = TextRow(1, "Key", "Name");
        for (var r = 2; r <= 501; r++) rows += TextRow(r, r.ToString(CultureInfo.InvariantCulture), "");

        var result = await ReadAsync<ContactUpdateRow>(Build(rows));

        result.Errors.Should().HaveCount(500).And.OnlyContain(error => error.Code == SpreadsheetErrorCodes.Required);
    }

    [Fact]
    public async Task HandlersOwnError_IsLocatedLikeThePackagesErrors_AndAppearsInTheResultAndItsAppError() {
        var bytes = Build(TextRow(1, "Name", "Key") + TextRow(2, "Anna", "K-404"));
        var result = await ReadAsync<ContactUpdateRow>(bytes);
        var row = result.Rows.Single();

        row.AddError(ContactUpdateRow.Columns.Key, "contacts.unknown_key", "No contact has this key.");

        row.IsValid.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Should().BeEquivalentTo(new {
            Code = "contacts.unknown_key", Worksheet = "Data", RowNumber = 2, Header = "Key", CellReference = "B2"
        });
        var data = result.ToAppError("workbook").Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.FieldErrors!["workbook"].Should().Equal("Row 2, Key (B2): No contact has this key.");
    }

    [Fact]
    public async Task ResultWithErrors_ConvertsToAValidationErrorKeyedByTheUploadMember_NamingRowColumnAndCell() {
        var bytes = Build(TextRow(1, "Name", "Balance", "Visits", "Newsletter", "Kind", "Active") +
                          TextRow(2, "Anna", "abc", "1", "x", "Company", "Ja"));
        var result = await ReadAsync<ContactRow>(bytes);

        var error = result.ToAppError("workbook");

        error.Kind.Should().Be(ErrorKind.Validation);
        var data = error.Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.FieldErrors!.Keys.Should().Equal("workbook");
        data.FieldErrors["workbook"].Should().Equal("Row 2, Balance (B2): 'abc' is not a number.");
    }

    [Fact]
    public async Task ResultWith500Errors_ConvertsTo100MessagesAndOneCountingTheRest_InTheApplicationsWording() {
        var rows = TextRow(1, "Key", "Name");
        for (var r = 2; r <= 501; r++) rows += TextRow(r, r.ToString(CultureInfo.InvariantCulture), "");
        var result = await ReadAsync<ContactUpdateRow>(Build(rows));

        var plain = (ValidationErrorData)result.ToAppError().Data!;
        var worded = (ValidationErrorData)result.ToAppError(format: e => $"Zeile {e.RowNumber}: {e.Header} fehlt.", maxMessages: 2).Data!;

        plain.FieldErrors![""].Should().HaveCount(101);
        plain.FieldErrors[""][^1].Should().Be("400 more problems are not listed.");
        worded.FieldErrors![""].Should().Equal("Zeile 2: Name fehlt.", "Zeile 3: Name fehlt.", "498 more problems are not listed.");
    }

    [Fact]
    public async Task OptionalColumnTheFileLacks_IsNotAnError_AndTheResultSaysWhichColumnsTheFileContains() {
        var bytes = Build(TextRow(1, "Key", "Name", "Phone") + TextRow(2, "1", "Anna", "123") + TextRow(3, "2", "Bert", ""));

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        result.HasErrors.Should().BeFalse();
        result.Contains(ContactUpdateRow.Columns.Phone).Should().BeTrue();
        result.Contains(ContactUpdateRow.Columns.Notes).Should().BeFalse();
        var bert = result.Records.Last();
        bert.Phone.Should().BeNull("an empty cell in a stated column clears the value");
        result.Rows[1].CellText(ContactUpdateRow.Columns.Phone).Should().BeEmpty();
    }

    [Fact]
    public async Task MissingColumnOfANonNullableProperty_IsOneErrorNamingTheHeader_AndNoRows() {
        var bytes = Build(TextRow(1, "Key", "Phone") + TextRow(2, "1", "123"));

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.ColumnMissing);
        error.Header.Should().Be("Name");
        error.Message.Should().Be("The column 'Name' is missing.");
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task HeaderCarriedTwice_IsOneErrorNamingIt_AndNoRows() {
        var bytes = Build(TextRow(1, "Key", "Name", "name ") + TextRow(2, "1", "Anna", "Anna"));

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.ColumnDuplicated);
        error.Header.Should().Be("Name");
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ComputedGetOnlyProperty_IsWrittenButItsColumnIsIgnoredWhenRead() {
        var bytes = Build(TextRow(1, "Name", "Balance", "Visits", "Newsletter", "Kind", "Active") +
                          TextRow(2, "Anna", "1", "1", "x", "Company", "Ja"));

        var result = await ReadAsync<ContactRow>(bytes);

        result.HasErrors.Should().BeFalse("a computed column is never missing");
        ContactRow.Columns.NameLength.IsReadable.Should().BeFalse();
        result.Contains(ContactRow.Columns.NameLength).Should().BeFalse();
    }

    [Fact]
    public async Task PositionalRecord_IsBuiltThroughItsConstructor() {
        var bytes = Build([new Sheet("Accounts", TextRow(1, "Iban", "Balance", "Holder") + TextRow(2, "AT61", "12,5", "Anna"))]);

        var account = (await ReadAsync<AccountRow>(bytes)).Records.Single();

        account.Should().Be(new AccountRow("AT61", 12.5m) { Holder = "Anna" });
    }

    [Fact]
    public async Task TextInANumberColumn_IsInvalidValueWithTheExpectedKindColumnAndCell() {
        var bytes = Build(TextRow(1, "Name", "Balance", "Visits", "Newsletter", "Kind", "Active") +
                          TextRow(2, "Anna", "abc", "1", "x", "Company", "Ja"));

        var error = (await ReadAsync<ContactRow>(bytes)).Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(SpreadsheetErrorCodes.InvalidValue);
        error.ExpectedKind.Should().Be(SpreadsheetValueKind.Number);
        error.Header.Should().Be("Balance");
        error.CellReference.Should().Be("B2");
        error.Message.Should().Be("'abc' is not a number.");
    }

    [Fact]
    public async Task UnknownLabelInAnEnumColumn_IsInvalidValueWithTheAllowedLabels() {
        var bytes = Build(TextRow(1, "Kind", "Agreed") + TextRow(2, "Club", "vielleicht"));

        var errors = (await ReadAsync<LenientRow>(bytes)).Errors.ToList();

        errors.Should().HaveCount(2).And.OnlyContain(error => error.Code == SpreadsheetErrorCodes.InvalidValue);
        errors[0].AllowedValues.Should().Equal("Private person", "Company", "Authority");
        errors[0].ExpectedKind.Should().Be(SpreadsheetValueKind.Choice);
        errors[1].AllowedValues.Should().Equal("Ja", "Nein");
    }

    [Fact]
    public async Task EmptyRequiredCell_IsRequiredWithTheCell() {
        var bytes = Build(TextRow(1, "Key", "Name") + TextRow(2, "1", "  "));

        var error = (await ReadAsync<ContactUpdateRow>(bytes)).Errors.Should().ContainSingle().Subject;

        error.Code.Should().Be(SpreadsheetErrorCodes.Required);
        error.CellReference.Should().Be("B2");
        error.ToString().Should().Be("Row 2, Name (B2): A value is required.");
    }

    [Fact]
    public async Task FileThatIsNoWorkbook_UnpacksTooLarge_OrHasTooManyRows_CarriesItsCodeAndLimit() {
        var rows = TextRow(1, "Key", "Name");
        for (var r = 2; r <= 12; r++) rows += TextRow(r, r.ToString(CultureInfo.InvariantCulture), "N");
        var bytes = Build(rows);

        var unreadable = await ReadAsync<ContactUpdateRow>("no workbook"u8.ToArray());
        var tooLarge = await ReadAsync<ContactUpdateRow>(bytes, new SpreadsheetReadOptions { MaxUnpackedBytes = 100 });
        var tooManyRows = await ReadAsync<ContactUpdateRow>(bytes, new SpreadsheetReadOptions { MaxRows = 10 });

        unreadable.Errors.Should().ContainSingle().Which.Code.Should().Be(SpreadsheetErrorCodes.Unreadable);
        tooLarge.Errors.Should().ContainSingle().Which.Limit.Should().Be(100);
        var limit = tooManyRows.Errors.Should().ContainSingle().Subject;
        limit.Code.Should().Be(SpreadsheetErrorCodes.TooManyRows);
        limit.Limit.Should().Be(10);
        limit.RowNumber.Should().Be(12);
        tooManyRows.Rows.Should().HaveCount(10, "the rows before the limit are kept");
    }

    [Fact]
    public async Task PartWhoseDeclaredSizeLies_IsTruncated_AndOneUnreadableError() {
        // The worksheet inflates to 8 MiB of spaces but declares 1,000 bytes: the zip reader stops at the declared size.
        var sheet = System.Text.Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><worksheet xmlns=\"{MainNamespace}\"><sheetData>")
            .Concat(Enumerable.Repeat((byte)' ', 8 << 20)).Concat("</sheetData></worksheet>"u8.ToArray()).ToArray();
        var bytes = WithEntry(Build(TextRow(1, "a")), "xl/worksheets/sheet1.xml", Deflate(sheet), 1_000, Crc32(sheet));

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be(SpreadsheetErrorCodes.Unreadable);
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task SharedRichTextAndUnreferencedCells_AreRead() {
        var bytes = Build(
            """<row r="1"><c t="inlineStr"><is><t>Key</t></is></c><c t="s"><v>0</v></c></row><row r="2"><c t="inlineStr"><is><t>1</t></is></c><c t="s"><v>1</v></c></row>""",
            sharedStrings: ["<si><t>Name</t></si>", "<si><r><t xml:space=\"preserve\">An</t></r><r><rPr><b/></rPr><t>na</t></r></si>"]);

        var result = await ReadAsync<ContactUpdateRow>(bytes);

        result.HasErrors.Should().BeFalse(string.Join("; ", result.Errors));
        result.Records.Single().Name.Should().Be("Anna");
    }

    [Fact]
    public async Task IntegralColumn_RejectsAFractionAndAValueOutOfRange() {
        var bytes = Build(TextRow(1, "Count", "Small") + $"<row r=\"2\">{Number("A2", "1.5")}{Number("B2", "256")}</row>" +
                          TextRow(3, "+12", "255"));

        var result = await ReadAsync<LenientRow>(bytes);

        result.Rows[0].Errors.Select(error => error.CellReference).Should().Equal("A2", "B2");
        result.Rows[1].Record!.Count.Should().Be(12);
        result.Rows[1].Record!.Small.Should().Be(255);
    }
}
