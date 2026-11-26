namespace Elarion.Spreadsheets;

/// <summary>
/// One column of a spreadsheet record, as its generated column table declares it. The generated half of a record holds
/// one per column as <c>T.Columns.{Property}</c>, so application code names a column without a string.
/// </summary>
/// <typeparam name="T">The record type the column belongs to.</typeparam>
public sealed class SpreadsheetColumn<T> where T : class {
    internal SpreadsheetColumn(
        int index,
        string header,
        string propertyName,
        SpreadsheetValueKind kind,
        bool isNullable,
        string? format,
        double? width,
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> choices) {
        Index = index;
        Header = header;
        PropertyName = propertyName;
        Kind = kind;
        IsNullable = isNullable;
        Format = format;
        Width = width;
        Aliases = aliases;
        Choices = choices;
    }

    /// <summary>The zero-based position of the column in the worksheet.</summary>
    public int Index { get; }

    /// <summary>The column's header.</summary>
    public string Header { get; }

    /// <summary>The name of the property the column maps.</summary>
    public string PropertyName { get; }

    /// <summary>The kind of value the column holds.</summary>
    public SpreadsheetValueKind Kind { get; }

    /// <summary>Whether the property accepts <see langword="null"/>, so an empty cell is a value and not a gap.</summary>
    public bool IsNullable { get; }

    /// <summary>The declared number format, or <see langword="null"/>.</summary>
    public string? Format { get; }

    /// <summary>The declared width in characters, or <see langword="null"/> when the writer measures it.</summary>
    public double? Width { get; }

    /// <summary>Older headers that still read as this column.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// The labels a <see cref="SpreadsheetValueKind.Choice"/> column offers, in declaration order (enum members, or the
    /// true and the false label of a boolean); empty for every other kind.
    /// </summary>
    public IReadOnlyList<string> Choices { get; }

    /// <inheritdoc />
    public override string ToString() {
        return Header;
    }
}
