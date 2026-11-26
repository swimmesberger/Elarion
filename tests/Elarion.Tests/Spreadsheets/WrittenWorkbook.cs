using System.IO.Compression;
using AwesomeAssertions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

/// <summary>
/// A workbook the package wrote, validated against the Open XML schema and opened through the Open XML SDK, so the tests
/// inspect what a spreadsheet application would read rather than the writer's own view.
/// </summary>
internal sealed class WrittenWorkbook : IDisposable {
    private readonly SpreadsheetDocument _document;

    private WrittenWorkbook(byte[] bytes) {
        Bytes = bytes;
        _document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var errors = new OpenXmlValidator().Validate(_document)
            .Select(error => $"{error.Path?.XPath}: {error.Description}")
            .ToList();
        errors.Should().BeEmpty("a written workbook must be valid Open XML");
    }

    public byte[] Bytes { get; }

    public WorkbookPart WorkbookPart => _document.WorkbookPart!;

    public IReadOnlyList<string> WorksheetNames =>
        [.. WorkbookPart.Workbook!.Sheets!.Elements<Sheet>().Select(sheet => sheet.Name!.Value!)];

    public static async Task<WrittenWorkbook> WriteAsync<T>(
        IEnumerable<T> records, SpreadsheetWriteOptions? options = null) where T : class, ISpreadsheetRecord<T> {
        using var stream = new MemoryStream();
        await SpreadsheetWorkbook.WriteAsync(stream, records, options, TestContext.Current.CancellationToken);
        return new WrittenWorkbook(stream.ToArray());
    }

    public static WrittenWorkbook From(byte[] bytes) {
        return new WrittenWorkbook(bytes);
    }

    public Worksheet Worksheet(int index = 0) {
        var sheet = WorkbookPart.Workbook!.Sheets!.Elements<Sheet>().ElementAt(index);
        return ((WorksheetPart)WorkbookPart.GetPartById(sheet.Id!.Value!)).Worksheet!;
    }

    public IReadOnlyList<Row> Rows(int worksheet = 0) {
        return [.. Worksheet(worksheet).GetFirstChild<SheetData>()!.Elements<Row>()];
    }

    public Cell Cell(string reference, int worksheet = 0) {
        return Worksheet(worksheet).Descendants<Cell>().Single(cell => cell.CellReference == reference);
    }

    public Cell? FindCell(string reference, int worksheet = 0) {
        return Worksheet(worksheet).Descendants<Cell>().SingleOrDefault(cell => cell.CellReference == reference);
    }

    /// <summary>The text of a cell as a spreadsheet application shows it before formatting.</summary>
    public static string Text(Cell cell) {
        return cell.DataType?.Value == CellValues.InlineString
            ? cell.InlineString!.Text!.Text
            : cell.CellValue?.Text ?? "";
    }

    public CellFormat CellFormat(Cell cell) {
        return CellFormatAt(cell.StyleIndex?.Value ?? 0);
    }

    public CellFormat CellFormatAt(uint index) {
        return WorkbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats!.Elements<CellFormat>().ElementAt((int)index);
    }

    public string? CustomFormatCode(uint numberFormatId) {
        return WorkbookPart.WorkbookStylesPart!.Stylesheet!.NumberingFormats?.Elements<NumberingFormat>()
            .SingleOrDefault(format => format.NumberFormatId?.Value == numberFormatId)?.FormatCode?.Value;
    }

    public IReadOnlyList<Column> Columns(int worksheet = 0) {
        return [.. Worksheet(worksheet).GetFirstChild<Columns>()!.Elements<Column>()];
    }

    public IReadOnlyList<DataValidation> DataValidations(int worksheet = 0) {
        return [.. Worksheet(worksheet).GetFirstChild<DataValidations>()?.Elements<DataValidation>() ?? []];
    }

    public DataValidation DataValidation(string column, int worksheet = 0) {
        return DataValidations(worksheet).Single(validation => validation.SequenceOfReferences!.InnerText!.StartsWith(
            column + "2:", StringComparison.Ordinal));
    }

    public Sheet Sheet(int index) {
        return WorkbookPart.Workbook!.Sheets!.Elements<Sheet>().ElementAt(index);
    }

    /// <summary>The raw XML of a part, for assertions on what the schema does not model (escapes, absent formulas).</summary>
    public string PartXml(string entryName) {
        using var archive = new ZipArchive(new MemoryStream(Bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
        return reader.ReadToEnd();
    }

    public IReadOnlyList<string> EntryNames() {
        using var archive = new ZipArchive(new MemoryStream(Bytes), ZipArchiveMode.Read);
        return [.. archive.Entries.Select(entry => entry.FullName)];
    }

    public void Dispose() {
        _document.Dispose();
    }
}
