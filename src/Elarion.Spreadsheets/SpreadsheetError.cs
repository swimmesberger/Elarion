using System.Globalization;
using System.Text;

namespace Elarion.Spreadsheets;

/// <summary>
/// One problem found while reading a workbook (ADR-0108, decisions 5 and 6): a stable <see cref="Code"/>, the data its
/// wording needs, an English default <see cref="Message"/>, and where it is — the worksheet, the row, the column header
/// and the cell reference of a problem with a cell, or the worksheet or the header of a problem with the file or a column.
/// </summary>
/// <remarks>
/// The constructor is public so a handler's tests can build a result by hand. <see cref="Message"/> does not repeat the
/// location; <see cref="ToString"/> prefixes it.
/// </remarks>
public sealed class SpreadsheetError {
    /// <summary>Creates an error.</summary>
    /// <param name="code">The stable code, one of <see cref="SpreadsheetErrorCodes"/> or the application's own.</param>
    /// <param name="message">The English default message, without the location.</param>
    /// <param name="worksheet">The worksheet, or <see langword="null"/> for a problem with the whole file.</param>
    /// <param name="rowNumber">The row number the spreadsheet shows, or <see langword="null"/>.</param>
    /// <param name="header">The column header, or <see langword="null"/>.</param>
    /// <param name="cellReference">The cell reference such as <c>C13</c>, or <see langword="null"/>.</param>
    /// <param name="expectedKind">The kind of value the column expects, for <see cref="SpreadsheetErrorCodes.InvalidValue"/>.</param>
    /// <param name="allowedValues">The values a choice column allows, for <see cref="SpreadsheetErrorCodes.InvalidValue"/>.</param>
    /// <param name="limit">The exceeded limit, for the <c>too_*</c> codes.</param>
    public SpreadsheetError(
        string code,
        string message,
        string? worksheet = null,
        int? rowNumber = null,
        string? header = null,
        string? cellReference = null,
        SpreadsheetValueKind? expectedKind = null,
        IReadOnlyList<string>? allowedValues = null,
        long? limit = null) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
        Worksheet = worksheet;
        RowNumber = rowNumber;
        Header = header;
        CellReference = cellReference;
        ExpectedKind = expectedKind;
        AllowedValues = allowedValues;
        Limit = limit;
    }

    /// <summary>The stable code (FR-050, FR-066).</summary>
    public string Code { get; }

    /// <summary>The English default message, without the location.</summary>
    public string Message { get; }

    /// <summary>The worksheet, or <see langword="null"/> for a problem with the whole file.</summary>
    public string? Worksheet { get; }

    /// <summary>The row number the spreadsheet shows, or <see langword="null"/>.</summary>
    public int? RowNumber { get; }

    /// <summary>The column header, or <see langword="null"/>.</summary>
    public string? Header { get; }

    /// <summary>The cell reference such as <c>C13</c>, or <see langword="null"/>.</summary>
    public string? CellReference { get; }

    /// <summary>The kind of value the column expects, for <see cref="SpreadsheetErrorCodes.InvalidValue"/>.</summary>
    public SpreadsheetValueKind? ExpectedKind { get; }

    /// <summary>The values a choice column allows, for <see cref="SpreadsheetErrorCodes.InvalidValue"/>.</summary>
    public IReadOnlyList<string>? AllowedValues { get; }

    /// <summary>
    /// The exceeded limit, for <see cref="SpreadsheetErrorCodes.TooLarge"/> (bytes),
    /// <see cref="SpreadsheetErrorCodes.TooManyRows"/> and <see cref="SpreadsheetErrorCodes.TooManyColumns"/>.
    /// </summary>
    public long? Limit { get; }

    /// <summary>The message prefixed with its location, for example <c>Row 13, E-mail (C13): …</c>.</summary>
    public override string ToString() {
        var location = new StringBuilder();
        if (RowNumber is { } row) location.Append("Row ").Append(row.ToString(CultureInfo.InvariantCulture));
        if (Header is not null) {
            if (location.Length > 0) location.Append(", ");
            location.Append(Header);
        }

        if (CellReference is not null) location.Append(" (").Append(CellReference).Append(')');

        return location.Length == 0 ? Message : location.Append(": ").Append(Message).ToString();
    }
}
