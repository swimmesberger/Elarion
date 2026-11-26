namespace Elarion.Spreadsheets;

/// <summary>Options of a <see cref="SpreadsheetWorkbook"/> write.</summary>
public sealed record SpreadsheetWriteOptions {
    /// <summary>The shared defaults.</summary>
    public static SpreadsheetWriteOptions Default { get; } = new();

    /// <summary>
    /// How many data rows below the header offer the dropdown list of an enum or a labelled boolean column, written
    /// records or not, so a template has the lists where a person fills it in (FR-021, FR-022). Every written row
    /// offers them too, however many there are. Must be zero or more.
    /// </summary>
    public int DropdownRows { get; init; } = 2000;

    /// <summary>
    /// How many records the writer reads ahead and measures for the widths of columns without a declared width (FR-019).
    /// These are the only records it holds in memory; the rest stream. Must be zero or more.
    /// </summary>
    public int WidthSampleRows { get; init; } = 200;

    /// <summary>The widest a measured column gets, in characters. Must be one or more.</summary>
    public double MaxColumnWidth { get; init; } = 80;
}
