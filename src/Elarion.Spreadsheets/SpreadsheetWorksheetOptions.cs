namespace Elarion.Spreadsheets;

/// <summary>
/// Options of one worksheet, added from records with
/// <see cref="SpreadsheetWorkbook.AddWorksheetAsync{T}(IEnumerable{T}, SpreadsheetWorksheetOptions?, CancellationToken)"/>
/// or from a run-time definition with
/// <see cref="SpreadsheetWorkbook.AddWorksheetAsync(SpreadsheetSheetDefinition, IEnumerable{IReadOnlyList{object}}, SpreadsheetWorksheetOptions?, CancellationToken)"/>.
/// </summary>
public sealed record SpreadsheetWorksheetOptions {
    /// <summary>
    /// The worksheet's name; <see langword="null"/> uses the name the record or the definition declares. It follows the
    /// rules of <see cref="SpreadsheetRecordAttribute.WorksheetName"/>, checked when the worksheet is added.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The record's columns this worksheet writes, named by their property name (<c>nameof(Record.Property)</c>, or
    /// <c>Record.Columns.Property.PropertyName</c>); <see langword="null"/> (the default) writes every column. The columns
    /// keep the record's order and close up, so a column a caller may not see is left out rather than written empty
    /// (FR-076). A name no column has throws an <see cref="ArgumentException"/>. A run-time definition already declares
    /// its columns per call and takes no filter: setting one for it throws an <see cref="ArgumentException"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new SpreadsheetWorksheetOptions {
    ///     ExcludedColumns = permissions.Holds(ElarionPermissions.Payments.Read) ? null : [nameof(InvoiceRow.PaidAmount)]
    /// };
    /// </code>
    /// </example>
    public IReadOnlyCollection<string>? IncludedColumns { get; init; }

    /// <summary>
    /// The record's columns this worksheet leaves out, named by their property name, applied after
    /// <see cref="IncludedColumns"/>; <see langword="null"/> (the default) leaves none out. A name no column has throws an
    /// <see cref="ArgumentException"/>, and so does a filter for a run-time definition.
    /// </summary>
    public IReadOnlyCollection<string>? ExcludedColumns { get; init; }

    /// <summary>
    /// Rows written above the header row, such as a title, the period an export covers or the account it is for
    /// (FR-082). Each row is a list of values for the written columns, from the first, typed as footer values are; an
    /// empty row leaves a blank line. The header row moves down by their number and still stays in place while
    /// scrolling, and the dropdown lists start below it. A reader takes the first non-empty row as the header row, so
    /// read such a worksheet back with <see cref="SpreadsheetWorksheetSelection.HeaderRow"/> set to the header's row
    /// number.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>>? TitleRows { get; init; }

    /// <summary>
    /// Rows written below the records, such as a total row whose label stands in a date column, or a second block of
    /// other values (FR-075). Each row is a list of values for the written columns, from the first, typed as the values
    /// of a run-time row are (see
    /// <see cref="SpreadsheetWorkbook.AddWorksheetAsync(SpreadsheetSheetDefinition, IEnumerable{IReadOnlyList{object}}, CancellationToken)"/>);
    /// a shorter row ends with empty cells. The dropdown lists of choice columns stop above them. A reader reads footer
    /// rows as data rows, so a worksheet meant to be imported again has none.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>>? FooterRows { get; init; }

    /// <summary>
    /// Whether the worksheet is hidden (FR-083): a spreadsheet application offers it under "Unhide" rather than as a
    /// tab, and a reader skips it, as it skips every hidden worksheet. A workbook needs at least one worksheet that is
    /// not hidden; disposing one whose worksheets are all hidden fails with an <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool Hidden { get; init; }
}
