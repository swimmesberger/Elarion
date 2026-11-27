using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets;

/// <summary>
/// A worksheet whose columns are known only at run time (ADR-0108, amendment 2026-11-27; FR-073): its name and its
/// columns, for <see cref="SpreadsheetWorkbook.AddWorksheetAsync(SpreadsheetSheetDefinition, IEnumerable{IReadOnlyList{object}}, CancellationToken)"/>.
/// Use it where a schema composes its columns from data — numbered slots, columns that depend on a setting — and a
/// <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> class would repeat that schema; the worksheet gets
/// the same header, formats, widths and dropdown lists a record's does.
/// </summary>
/// <remarks>
/// A definition only writes. To read such a worksheet back, read it as plain rows
/// (<see cref="SpreadsheetReader.ReadRowsAsync"/>) and match the headers with the same column list.
/// </remarks>
public sealed class SpreadsheetSheetDefinition {
    /// <summary>Creates a worksheet definition.</summary>
    /// <param name="name">
    /// The worksheet's name: not empty, at most 31 characters, none of <c>[ ] : * ? / \</c>, no apostrophe at either end.
    /// </param>
    /// <param name="columns">The columns, in column order.</param>
    /// <exception cref="ArgumentException">The name is not a valid worksheet name, or a column is <see langword="null"/>.</exception>
    public SpreadsheetSheetDefinition(string name, IEnumerable<SpreadsheetColumnDefinition> columns) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(columns);
        if (SpreadsheetCells.ValidateWorksheetName(name) is { } reason) throw new ArgumentException(reason, nameof(name));

        SpreadsheetColumnDefinition[] list = [.. columns];
        if (list.Any(static column => column is null)) throw new ArgumentException("A column must not be null.", nameof(columns));

        Name = name;
        Columns = list;
    }

    /// <summary>The worksheet's name.</summary>
    public string Name { get; }

    /// <summary>The columns, in column order.</summary>
    public IReadOnlyList<SpreadsheetColumnDefinition> Columns { get; }
}

/// <summary>
/// One column of a <see cref="SpreadsheetSheetDefinition"/>: its header, the kind of value it holds, and the number
/// format, width and dropdown list a <see cref="SpreadsheetColumnAttribute">[SpreadsheetColumn]</see> declares for a
/// record's column. Create one with a factory method; set <see cref="Width"/> with <c>with</c>.
/// </summary>
/// <example>
/// <code>
/// SpreadsheetColumnDefinition.Number("Area", format: "#,##0.00") with { Width = 12 }
/// </code>
/// </example>
public sealed record SpreadsheetColumnDefinition {
    private readonly double? _width;

    private SpreadsheetColumnDefinition(string header, SpreadsheetValueKind kind, string? format, IReadOnlyList<string> choices) {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
        Kind = kind;
        Format = format;
        Choices = choices;
    }

    /// <summary>The column's header.</summary>
    public string Header { get; }

    /// <summary>
    /// The kind of value the column holds, which decides its cell format: the text format for text and choice columns,
    /// <see cref="Format"/> or the viewer's notation for number, date, date-and-time and time columns.
    /// </summary>
    public SpreadsheetValueKind Kind { get; }

    /// <summary>The number format of a number, date, date-and-time or time column, or <see langword="null"/>.</summary>
    public string? Format { get; }

    /// <summary>
    /// The column's width in characters; <see langword="null"/> (the default) measures the header and the first
    /// <see cref="SpreadsheetWriteOptions.WidthSampleRows"/> written values. Must be more than zero.
    /// </summary>
    public double? Width {
        get => _width;
        init => _width = value is null or > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Width), value, "A column width must be more than zero.");
    }

    /// <summary>The labels a choice column offers in its dropdown list, in order; empty for every other kind.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>A text column, with the text format, so a value typed below the data stays text.</summary>
    public static SpreadsheetColumnDefinition Text(string header) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Text, null, []);
    }

    /// <summary>A number column.</summary>
    /// <param name="header">The header.</param>
    /// <param name="format">The number format, for example <c>#,##0.00</c>; <see langword="null"/> for the default.</param>
    public static SpreadsheetColumnDefinition Number(string header, string? format = null) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Number, format, []);
    }

    /// <summary>A boolean column.</summary>
    public static SpreadsheetColumnDefinition Boolean(string header) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Boolean, null, []);
    }

    /// <summary>A date column.</summary>
    /// <param name="header">The header.</param>
    /// <param name="format">The number format, for example <c>dd.mm.yyyy</c>; <see langword="null"/> shows the viewer's own notation.</param>
    public static SpreadsheetColumnDefinition Date(string header, string? format = null) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Date, format, []);
    }

    /// <summary>A date-and-time column.</summary>
    /// <param name="header">The header.</param>
    /// <param name="format">The number format; <see langword="null"/> shows the viewer's own notation.</param>
    public static SpreadsheetColumnDefinition DateTime(string header, string? format = null) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.DateTime, format, []);
    }

    /// <summary>A time column.</summary>
    /// <param name="header">The header.</param>
    /// <param name="format">The number format; <see langword="null"/> shows the viewer's own notation.</param>
    public static SpreadsheetColumnDefinition Time(string header, string? format = null) {
        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Time, format, []);
    }

    /// <summary>
    /// A text column that offers <paramref name="choices"/> as a dropdown list, written as the values of an enum column
    /// are: inline when they fit, otherwise on the hidden lists worksheet.
    /// </summary>
    /// <param name="header">The header.</param>
    /// <param name="choices">The labels, in the order offered; none may be <see langword="null"/>.</param>
    public static SpreadsheetColumnDefinition Choice(string header, IEnumerable<string> choices) {
        ArgumentNullException.ThrowIfNull(choices);
        string[] labels = [.. choices];
        if (labels.Any(static label => label is null)) throw new ArgumentException("A choice must not be null.", nameof(choices));

        return new SpreadsheetColumnDefinition(header, SpreadsheetValueKind.Choice, null, labels);
    }
}
