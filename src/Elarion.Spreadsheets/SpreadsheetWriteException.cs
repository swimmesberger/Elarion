namespace Elarion.Spreadsheets;

/// <summary>
/// Thrown when records cannot be written as a workbook: a value a spreadsheet cannot hold (text longer than 32,767
/// characters, a date before 1900-01-01, <c>NaN</c> or an infinity, a row beyond 1,048,576), an invalid worksheet name,
/// or two worksheets whose names are equal ignoring case. The message and the properties name the worksheet, and for a
/// value the row, the column and the cell.
/// </summary>
/// <remarks>
/// Writing is forward-only, so a workbook whose write failed is incomplete; discard what was written to the
/// destination.
/// </remarks>
public sealed class SpreadsheetWriteException : Exception {
    /// <summary>Creates the exception with a message.</summary>
    public SpreadsheetWriteException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public SpreadsheetWriteException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception for a worksheet, a row, a column or a cell.</summary>
    public SpreadsheetWriteException(
        string message, string? worksheet, int? rowNumber = null, string? header = null, string? cellReference = null)
        : base(message) {
        Worksheet = worksheet;
        RowNumber = rowNumber;
        Header = header;
        CellReference = cellReference;
    }

    /// <summary>The worksheet being written, or <see langword="null"/>.</summary>
    public string? Worksheet { get; }

    /// <summary>The row number as the spreadsheet shows it (the header is row 1), or <see langword="null"/>.</summary>
    public int? RowNumber { get; }

    /// <summary>The header of the column, or <see langword="null"/>.</summary>
    public string? Header { get; }

    /// <summary>The cell reference, for example <c>C13</c>, or <see langword="null"/>.</summary>
    public string? CellReference { get; }
}
