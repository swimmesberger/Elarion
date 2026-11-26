namespace Elarion.Spreadsheets;

/// <summary>Options of a <see cref="SpreadsheetWorkbook"/> write.</summary>
public sealed record SpreadsheetWriteOptions {
    /// <summary>The shared defaults.</summary>
    public static SpreadsheetWriteOptions Default { get; } = new();

    /// <summary>
    /// How many records the writer reads ahead and measures for the widths of columns without a declared width (FR-019).
    /// These are the only records it holds in memory; the rest stream. Must be zero or more.
    /// </summary>
    public int WidthSampleRows { get; init; } = 200;

    /// <summary>The widest a measured column gets, in characters. Must be one or more.</summary>
    public double MaxColumnWidth { get; init; } = 80;
}
