namespace Elarion.Spreadsheets;

/// <summary>
/// Configures the column a property of a <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> maps to:
/// its header, its position, its number format and width, the older headers that still read as it, and the labels of a
/// boolean.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SpreadsheetColumnAttribute(string? header = null) : Attribute {
    /// <summary>
    /// The column's header; <see langword="null"/> uses the property's <c>[Display(Name)]</c>, else its name. Headers and
    /// aliases of one record must differ ignoring case (<c>ELSHEET003</c>).
    /// </summary>
    public string? Header { get; } = header;

    /// <summary>
    /// The zero-based position of the column among the record's columns. Columns without an order keep their
    /// declaration order and fill the positions the ordered ones leave free. A negative value (the default) keeps the
    /// declaration order.
    /// </summary>
    public int Order { get; set; } = -1;

    /// <summary>
    /// The number format of a number, date, date-and-time or time column, in the spreadsheet's format notation, for
    /// example <c>#,##0.00</c> or <c>yyyy-mm-dd</c>. A date, date-and-time or time column without one shows its values
    /// in the viewer's own notation. Ignored on text, boolean and choice columns, which keep the text format.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// The column's width in characters; zero or less (the default) measures the header and the first
    /// <c>SpreadsheetWriteOptions.WidthSampleRows</c> written values.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Older headers that a file may carry instead of <see cref="Header"/>; when a file carries both, the header wins and
    /// the alias column is ignored.
    /// </summary>
    public string[]? Aliases { get; set; }

    /// <summary>
    /// The text a <see langword="true"/> boolean is written as, and read from besides the built-in words. Requires
    /// <see cref="FalseLabel"/> and a <c>bool</c> property (<c>ELSHEET005</c>).
    /// </summary>
    public string? TrueLabel { get; set; }

    /// <summary>
    /// The text a <see langword="false"/> boolean is written as, and read from besides the built-in words. Requires
    /// <see cref="TrueLabel"/> and a <c>bool</c> property (<c>ELSHEET005</c>).
    /// </summary>
    public string? FalseLabel { get; set; }
}
