namespace Elarion.Spreadsheets;

/// <summary>
/// The stable codes of <see cref="SpreadsheetError"/> (FR-066), in the framework's <c>area.snake_case</c> form, so an
/// application words every problem of an import in its own language without parsing a message.
/// </summary>
/// <remarks>
/// The codes belong to a read result, not to an <c>AppError</c> a handler returns, so they live here and not in
/// <c>ErrorCodes</c>. An application's own row errors (<c>SpreadsheetRow&lt;T&gt;.AddError</c>) use codes of its own.
/// </remarks>
public static class SpreadsheetErrorCodes {
    /// <summary>The file cannot be read as a workbook: no workbook, damaged, truncated or password-protected.</summary>
    public const string Unreadable = "spreadsheet.unreadable";

    /// <summary>The workbook unpacks to more than <see cref="SpreadsheetReadOptions.MaxUnpackedBytes"/>; see <see cref="SpreadsheetError.Limit"/>.</summary>
    public const string TooLarge = "spreadsheet.too_large";

    /// <summary>The worksheet has more data rows than <see cref="SpreadsheetReadOptions.MaxRows"/>; the rows before the limit are kept.</summary>
    public const string TooManyRows = "spreadsheet.too_many_rows";

    /// <summary>A row read without a record has more columns than <see cref="SpreadsheetReadOptions.MaxColumns"/>.</summary>
    public const string TooManyColumns = "spreadsheet.too_many_columns";

    /// <summary>Every worksheet of the workbook is hidden.</summary>
    public const string NoVisibleWorksheet = "spreadsheet.no_visible_worksheet";

    /// <summary>No visible worksheet has the requested name; see <see cref="SpreadsheetError.Worksheet"/>.</summary>
    public const string WorksheetNotFound = "spreadsheet.worksheet_not_found";

    /// <summary>The header of a column whose property is not nullable is missing; see <see cref="SpreadsheetError.Header"/>.</summary>
    public const string ColumnMissing = "spreadsheet.column_missing";

    /// <summary>The file carries a mapped header more than once; see <see cref="SpreadsheetError.Header"/>.</summary>
    public const string ColumnDuplicated = "spreadsheet.column_duplicated";

    /// <summary>A cell of a column whose property is not nullable is empty.</summary>
    public const string Required = "spreadsheet.required";

    /// <summary>
    /// A cell does not convert to its column's kind; see <see cref="SpreadsheetError.ExpectedKind"/> and, for a choice,
    /// <see cref="SpreadsheetError.AllowedValues"/>.
    /// </summary>
    public const string InvalidValue = "spreadsheet.invalid_value";

    /// <summary>A record violates a DataAnnotations rule; the message is the validator's.</summary>
    public const string Validation = "validation";
}
