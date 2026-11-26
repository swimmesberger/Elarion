namespace Elarion.Spreadsheets;

/// <summary>Which worksheet of a workbook a read takes, and which of its rows holds the headers.</summary>
public sealed record SpreadsheetWorksheetSelection {
    /// <summary>
    /// The worksheet's name, matched ignoring case; <see langword="null"/> takes the first visible worksheet (FR-029). A
    /// name that no visible worksheet has is one <see cref="SpreadsheetErrorCodes.WorksheetNotFound"/> error (FR-067).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The row number, as the spreadsheet shows it, of the header row of a record read; <see langword="null"/> takes the
    /// first non-empty row (FR-030). Rows above it are ignored. A read without a record ignores it.
    /// </summary>
    public int? HeaderRow { get; init; }
}
