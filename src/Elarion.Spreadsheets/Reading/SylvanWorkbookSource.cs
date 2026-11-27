using System.Text;
using Sylvan.Data.Excel;

namespace Elarion.Spreadsheets.Reading;

/// <summary>
/// The <see cref="IWorkbookSource"/> over Sylvan.Data.Excel (ADR-0108, decision 3), opened on a buffer the
/// <see cref="WorkbookInspector"/> has checked.
/// </summary>
/// <remarks>
/// <para>
/// The library gets the buffer through its synchronous <c>Create(Stream, type)</c>, which reads a seekable stream in
/// place; <c>CreateAsync</c> would copy it again. It reads without headers, so the package resolves the header row
/// itself, and it skips hidden worksheets; the inspector adds the <c>veryHidden</c> ones it would show.
/// </para>
/// <para>
/// A cell's kind comes from the library's data type, and a number is a date when its format, built-in or custom, is a
/// date or time format. A time without a date takes its value from the fraction of the number, because the library's
/// date conversion of such a value depends on the date system and the file format.
/// </para>
/// </remarks>
internal sealed class SylvanWorkbookSource : IWorkbookSource {
    private static readonly DateTime DateOrigin = new(1899, 12, 30);

    private readonly ExcelDataReader _reader;
    private readonly IReadOnlySet<string> _veryHidden;

    private SylvanWorkbookSource(ExcelDataReader reader, IReadOnlyList<string> visible, IReadOnlySet<string> veryHidden) {
        _reader = reader;
        VisibleWorksheets = visible;
        _veryHidden = veryHidden;
    }

    static SylvanWorkbookSource() {
        // The .xls reader needs Windows-1252 in a static initializer; without the code-page provider that initializer
        // fails once and the type stays unusable for the life of the process. Registering is process-wide and idempotent.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> VisibleWorksheets { get; }

    /// <inheritdoc />
    public int RowNumber => _reader.RowNumber;

    /// <inheritdoc />
    public int CellCount => _reader.RowFieldCount;

    /// <summary>Opens the inspected workbook in <paramref name="content"/>.</summary>
    /// <exception cref="UnreadableWorkbookException">The library fails on the content.</exception>
    public static SylvanWorkbookSource Open(MemoryStream content, WorkbookInspection inspection) {
        var type = inspection.Format switch {
            WorkbookFormat.Xlsb => ExcelWorkbookType.ExcelBinary,
            WorkbookFormat.Xls => ExcelWorkbookType.Excel,
            _ => ExcelWorkbookType.ExcelXml
        };

        ExcelDataReader? reader = null;
        try {
            content.Position = 0;
            reader = ExcelDataReader.Create(content, type, new ExcelDataReaderOptions { Schema = ExcelSchema.NoHeaders });
            var visible = reader.WorksheetNames.Where(name => !inspection.VeryHiddenWorksheets.Contains(name)).ToArray();
            return new SylvanWorkbookSource(reader, visible, inspection.VeryHiddenWorksheets);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            reader?.Dispose();
            throw new UnreadableWorkbookException(exception);
        }
    }

    /// <inheritdoc />
    public bool OpenWorksheet(string name) {
        if (_veryHidden.Contains(name)) return false;

        try {
            return _reader.TryOpenWorksheet(name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new UnreadableWorkbookException(exception);
        }
    }

    /// <inheritdoc />
    public bool ReadRow() {
        try {
            return _reader.Read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new UnreadableWorkbookException(exception);
        }
    }

    /// <inheritdoc />
    public SpreadsheetCell GetCell(int ordinal) {
        if (ordinal >= _reader.RowFieldCount) return SpreadsheetCell.Blank;

        try {
            return _reader.GetExcelDataType(ordinal) switch {
                ExcelDataType.String => SpreadsheetCell.FromText(_reader.GetString(ordinal)),
                ExcelDataType.Numeric => ReadNumber(ordinal),
                ExcelDataType.DateTime => SpreadsheetCell.FromDate(_reader.GetDateTime(ordinal), _reader.GetFormat(ordinal)?.Format),
                ExcelDataType.Boolean => SpreadsheetCell.FromBoolean(_reader.GetBoolean(ordinal)),
                ExcelDataType.Error => SpreadsheetCell.FromError(ErrorText(_reader.GetFormulaError(ordinal))),
                _ => SpreadsheetCell.Blank
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new UnreadableWorkbookException(exception);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        _reader.Dispose();
    }

    private SpreadsheetCell ReadNumber(int ordinal) {
        var value = _reader.GetDouble(ordinal);
        if (!double.IsFinite(value)) return SpreadsheetCell.FromError("#NUM!");

        // The cell's own format, so a caller can show the value as the file does (FR-080).
        var format = _reader.GetFormat(ordinal);
        if (format?.Kind is not (FormatKind.Date or FormatKind.Time) || value < 0)
            return SpreadsheetCell.FromNumber(value, format?.Format);

        if (value < 1) return SpreadsheetCell.FromDays(value, format.Format);

        // The library knows the workbook's date system (1900 or 1904); its date carries the day, the fraction the time.
        DateTime date;
        try {
            date = _reader.GetDateTime(ordinal).Date;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or ArgumentException
                                              or OverflowException) {
            return SpreadsheetCell.FromNumber(value, format.Format);
        }

        return SpreadsheetCell.FromDays((date - DateOrigin).Days + (value - Math.Floor(value)), format.Format);
    }

    private static string ErrorText(ExcelErrorCode code) {
        return code switch {
            ExcelErrorCode.Null => "#NULL!",
            ExcelErrorCode.DivideByZero => "#DIV/0!",
            ExcelErrorCode.Value => "#VALUE!",
            ExcelErrorCode.Reference => "#REF!",
            ExcelErrorCode.Name => "#NAME?",
            ExcelErrorCode.Number => "#NUM!",
            ExcelErrorCode.NotAvailable => "#N/A",
            _ => "#ERROR!"
        };
    }
}
