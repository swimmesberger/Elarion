namespace Elarion.Spreadsheets;

/// <summary>
/// The result of reading a worksheet without a record (ADR-0108, decision 9): its non-empty rows as typed cells, for a
/// foreign layout such as a bank's statement export, whose header row the application finds itself.
/// </summary>
/// <remarks>The constructor is public so a handler's tests can build a result by hand.</remarks>
/// <example>
/// <code>
/// var sheet = await reader.ReadRowsAsync(request.Statement.OpenReadStream(), cancellationToken: ct);
/// if (sheet.FileErrors.Count > 0) return AppError.Validation(sheet.FileErrors[0].Message);
/// var header = sheet.Rows.First(row => row.Cells.Any(cell => cell.Text == "Booking date"));
/// foreach (var row in sheet.Rows.Where(row => row.RowNumber > header.RowNumber)) { … }
/// </code>
/// </example>
public sealed class SpreadsheetRowsResult {
    /// <summary>Creates a result.</summary>
    /// <param name="worksheet">The worksheet read, or <see langword="null"/> when none could be.</param>
    /// <param name="rows">The non-empty rows, in row order.</param>
    /// <param name="fileErrors">The errors about the file and its limits.</param>
    public SpreadsheetRowsResult(string? worksheet, IReadOnlyList<SpreadsheetCellRow> rows, IReadOnlyList<SpreadsheetError>? fileErrors = null) {
        ArgumentNullException.ThrowIfNull(rows);
        Worksheet = worksheet;
        Rows = rows;
        FileErrors = fileErrors ?? [];
    }

    /// <summary>The worksheet read, or <see langword="null"/> when none could be.</summary>
    public string? Worksheet { get; }

    /// <summary>The non-empty rows read before any limit, in row order (FR-058).</summary>
    public IReadOnlyList<SpreadsheetCellRow> Rows { get; }

    /// <summary>
    /// The errors that kept the file or the rest of the worksheet from being read, the same as a record read's
    /// (unreadable, too large, no such worksheet, too many rows), plus <see cref="SpreadsheetErrorCodes.TooManyColumns"/>.
    /// </summary>
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }
}

/// <summary>One non-empty row of a worksheet read without a record.</summary>
public sealed class SpreadsheetCellRow {
    /// <summary>Creates a row.</summary>
    /// <param name="rowNumber">The row number the spreadsheet shows.</param>
    /// <param name="cells">The row's cells from column A to its last non-blank cell.</param>
    public SpreadsheetCellRow(int rowNumber, IReadOnlyList<SpreadsheetCell> cells) {
        ArgumentNullException.ThrowIfNull(cells);
        RowNumber = rowNumber;
        Cells = cells;
    }

    /// <summary>The row number the spreadsheet shows.</summary>
    public int RowNumber { get; }

    /// <summary>The row's cells from column A to its last non-blank cell; a gap is a blank cell.</summary>
    public IReadOnlyList<SpreadsheetCell> Cells { get; }

    /// <summary>The cell at the zero-based <paramref name="column"/>, or a blank cell beyond the last one.</summary>
    public SpreadsheetCell this[int column] => column >= 0 && column < Cells.Count ? Cells[column] : SpreadsheetCell.Blank;
}
