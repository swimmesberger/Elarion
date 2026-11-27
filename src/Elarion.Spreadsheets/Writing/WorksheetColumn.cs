namespace Elarion.Spreadsheets.Writing;

/// <summary>
/// One column of a worksheet as the writer sees it, whatever declared it: a record's generated column table or a
/// <see cref="SpreadsheetSheetDefinition"/> built at run time. Its position in the worksheet is its index in the list.
/// </summary>
internal sealed record WorksheetColumn(
    string Header,
    SpreadsheetValueKind Kind,
    string? Format,
    double? Width,
    IReadOnlyList<string> Choices) {
    /// <summary>Whether the column offers a dropdown list of its choices.</summary>
    public bool IsDropdown => Kind == SpreadsheetValueKind.Choice && Choices.Count > 0;

    /// <summary>
    /// The format the column's data cells use: the declared one for a number, date, date-and-time or time column, none
    /// for the others, which keep the text format or the default.
    /// </summary>
    public string? EffectiveFormat => Kind is SpreadsheetValueKind.Integer or SpreadsheetValueKind.Number
        or SpreadsheetValueKind.Date or SpreadsheetValueKind.DateTime or SpreadsheetValueKind.Time
        ? Format
        : null;
}
