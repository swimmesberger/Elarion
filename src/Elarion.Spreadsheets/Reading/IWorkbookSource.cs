namespace Elarion.Spreadsheets.Reading;

/// <summary>
/// An opened workbook as the reader sees it (ADR-0108, decision 3): its visible worksheets, and the rows and typed cells
/// of one worksheet at a time. It keeps the reader library behind the package boundary, so no library type reaches the
/// public API and replacing the library changes neither a record nor a handler.
/// </summary>
/// <remarks>
/// An implementation turns every exception the library throws on broken input into a
/// <see cref="UnreadableWorkbookException"/>, so a damaged file becomes one <c>spreadsheet.unreadable</c> error. An
/// instance is not thread-safe.
/// </remarks>
internal interface IWorkbookSource : IDisposable {
    /// <summary>The names of the visible worksheets, in workbook order.</summary>
    IReadOnlyList<string> VisibleWorksheets { get; }

    /// <summary>Positions the source before the first row of the visible worksheet <paramref name="name"/>.</summary>
    /// <returns><see langword="false"/> when no visible worksheet has that name.</returns>
    bool OpenWorksheet(string name);

    /// <summary>Advances to the next row of the open worksheet, including rows without cells.</summary>
    bool ReadRow();

    /// <summary>The one-based number of the current row, as the spreadsheet shows it.</summary>
    int RowNumber { get; }

    /// <summary>The number of cell positions in the current row: the last cell's column plus one.</summary>
    int CellCount { get; }

    /// <summary>The cell of the current row at the zero-based column <paramref name="ordinal"/>.</summary>
    SpreadsheetCell GetCell(int ordinal);
}

/// <summary>Thrown by an <see cref="IWorkbookSource"/> when the library fails on the content of the file.</summary>
internal sealed class UnreadableWorkbookException(Exception inner)
    : Exception("The workbook cannot be read: " + inner.Message, inner);
