using System.ComponentModel;

namespace Elarion.Spreadsheets.Generated;

/// <summary>
/// Builds the column table of a generated <c>{T}SpreadsheetRowMapper</c>. Generated code calls it; application code does
/// not.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SpreadsheetColumns {
    /// <summary>Creates the column at <paramref name="index"/> of record <typeparamref name="T"/>.</summary>
    /// <param name="index">The zero-based position of the column.</param>
    /// <param name="header">The header.</param>
    /// <param name="propertyName">The mapped property.</param>
    /// <param name="kind">The kind of value.</param>
    /// <param name="isNullable">Whether the property accepts <see langword="null"/>.</param>
    /// <param name="format">The declared number format, or <see langword="null"/>.</param>
    /// <param name="width">The declared width in characters; zero or less when the writer measures it.</param>
    /// <param name="aliases">Older headers that still read as the column.</param>
    /// <param name="choices">The labels of a choice column; empty otherwise.</param>
    public static SpreadsheetColumn<T> Create<T>(
        int index,
        string header,
        string propertyName,
        SpreadsheetValueKind kind,
        bool isNullable,
        string? format,
        double width,
        string[] aliases,
        string[] choices) where T : class {
        return new SpreadsheetColumn<T>(
            index, header, propertyName, kind, isNullable, format, width > 0 ? width : null, aliases, choices);
    }
}
