using System.Globalization;
using AwesomeAssertions;
using DocumentFormat.OpenXml.Spreadsheet;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

public sealed class SpreadsheetTemplateTests {
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Template_HoldsTheHeaderRow_AndTheEnumColumnOffersTheLabelsInTheFirst2000Rows() {
        using var workbook = await WrittenWorkbook.WriteAsync(Array.Empty<ContactRow>());

        workbook.Rows().Should().ContainSingle();
        WrittenWorkbook.Text(workbook.Cell("H1")).Should().Be("Kind");
        var kind = workbook.DataValidation("H");
        kind.Type!.Value.Should().Be(DataValidationValues.List);
        kind.SequenceOfReferences!.InnerText.Should().Be("H2:H2001");
        kind.Formula1!.Text.Should().Be("\"Private person,Company,Authority\"");
        kind.AllowBlank!.Value.Should().BeTrue();
    }

    [Fact]
    public async Task Template_KeepsTheColumnFormatsAndWidths() {
        using var workbook = await WrittenWorkbook.WriteAsync(Array.Empty<ContactRow>());

        var columns = workbook.Columns();
        columns.Should().HaveCount(10);
        columns.Should().OnlyContain(column => column.Width!.Value > 0);
        workbook.CellFormatAt(columns[0].Style!.Value).NumberFormatId!.Value.Should().Be(49u, "a text column stays text");
        workbook.CellFormatAt(columns[3].Style!.Value).NumberFormatId!.Value.Should().Be(14u, "a date column stays a date");
    }

    [Fact]
    public async Task LabelledBoolean_IsWrittenAsItsLabels_AndOffersBothAsADropdown() {
        var records = new[] {
            new ContactRow { Name = "Anna", Active = true },
            new ContactRow { Name = "Bert", Active = false }
        };

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        WrittenWorkbook.Text(workbook.Cell("I2")).Should().Be("Ja");
        WrittenWorkbook.Text(workbook.Cell("I3")).Should().Be("Nein");
        var active = workbook.DataValidation("I");
        active.SequenceOfReferences!.InnerText.Should().Be("I2:I2001");
        active.Formula1!.Text.Should().Be("\"Ja,Nein\"");
        workbook.DataValidations().Should().HaveCount(2, "only the enum and the labelled boolean offer lists");
    }

    [Fact]
    public async Task SixtyLabelsLongerThan255Characters_AreStillAllOffered_FromOneHiddenWorksheet() {
        using var workbook = await WrittenWorkbook.WriteAsync(Array.Empty<RegionRow>());

        var labels = Enumerable.Range(1, 60)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"Sales region number {i:00}"))
            .ToList();
        string.Join(',', labels).Length.Should().BeGreaterThan(255);

        workbook.WorksheetNames.Should().Equal("Regions", "Lists");
        workbook.Sheet(1).State!.Value.Should().Be(SheetStateValues.Hidden);
        workbook.Rows(worksheet: 1).Select(row => WrittenWorkbook.Text(row.Elements<Cell>().Single()))
            .Should().Equal(labels);

        // Both columns of the same enum share the one list.
        workbook.DataValidation("B").Formula1!.Text.Should().Be("'Lists'!$A$1:$A$60");
        workbook.DataValidation("C").Formula1!.Text.Should().Be("'Lists'!$A$1:$A$60");
        workbook.DataValidation("B").SequenceOfReferences!.InnerText.Should().Be("B2:B2001");
    }

    [Fact]
    public async Task HiddenListsWorksheet_TakesANameNoOtherWorksheetHas() {
        using var stream = new MemoryStream();
        await using (var writer = SpreadsheetWorkbook.Create(stream)) {
            await writer.AddWorksheetAsync(
                [new DataRow { Value = "x" }], new SpreadsheetWorksheetOptions { Name = "lists" }, CancellationToken);
            await writer.AddWorksheetAsync(Array.Empty<RegionRow>(), cancellationToken: CancellationToken);
        }

        using var workbook = WrittenWorkbook.From(stream.ToArray());

        workbook.WorksheetNames.Should().Equal("lists", "Regions", "Lists 2");
        workbook.DataValidation("B", worksheet: 1).Formula1!.Text.Should().Be("'Lists 2'!$A$1:$A$60");
        workbook.DataValidations(worksheet: 0).Should().BeEmpty();
    }

    [Fact]
    public async Task ExportOf5000Records_OffersTheDropdownOnEveryRow() {
        var records = Enumerable.Range(0, 5000)
            .Select(i => new ContactRow { Name = i.ToString(CultureInfo.InvariantCulture), Kind = ContactKind.Authority });

        using var workbook = await WrittenWorkbook.WriteAsync(records);

        workbook.Rows().Should().HaveCount(5001);
        workbook.DataValidation("H").SequenceOfReferences!.InnerText.Should().Be("H2:H5001");
        workbook.DataValidation("I").SequenceOfReferences!.InnerText.Should().Be("I2:I5001");
    }

    [Fact]
    public async Task DropdownRows_SetsHowManyRowsATemplateOffers() {
        using var ten = await WrittenWorkbook.WriteAsync(
            Array.Empty<ContactRow>(), new SpreadsheetWriteOptions { DropdownRows = 10 });
        ten.DataValidation("H").SequenceOfReferences!.InnerText.Should().Be("H2:H11");

        using var none = await WrittenWorkbook.WriteAsync(
            Array.Empty<ContactRow>(), new SpreadsheetWriteOptions { DropdownRows = 0 });
        none.DataValidations().Should().BeEmpty();

        var act = () => SpreadsheetWorkbook.Create(new MemoryStream(), new SpreadsheetWriteOptions { DropdownRows = -1 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SameTemplateWrittenTwice_IsByteIdentical() {
        using var first = await WrittenWorkbook.WriteAsync(Array.Empty<RegionRow>());
        using var second = await WrittenWorkbook.WriteAsync(Array.Empty<RegionRow>());

        second.Bytes.Should().Equal(first.Bytes);
    }
}
