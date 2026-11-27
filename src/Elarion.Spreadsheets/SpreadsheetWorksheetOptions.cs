namespace Elarion.Spreadsheets;

/// <summary>Options of one worksheet added with <see cref="SpreadsheetWorkbook.AddWorksheetAsync{T}(IEnumerable{T}, SpreadsheetWorksheetOptions?, CancellationToken)"/>.</summary>
public sealed record SpreadsheetWorksheetOptions {
    /// <summary>
    /// The worksheet's name; <see langword="null"/> uses the name the record declares. It follows the rules of
    /// <see cref="SpreadsheetRecordAttribute.WorksheetName"/>, checked when the worksheet is added.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The record's columns this worksheet writes, named by their property name (<c>nameof(Record.Property)</c>, or
    /// <c>Record.Columns.Property.PropertyName</c>); <see langword="null"/> (the default) writes every column. The columns
    /// keep the record's order and close up, so a column a caller may not see is left out rather than written empty
    /// (FR-076). A name no column has throws an <see cref="ArgumentException"/>.
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
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public IReadOnlyCollection<string>? ExcludedColumns { get; init; }

    /// <summary>
    /// Rows written below the records, such as a total row whose label stands in a date column, or a second block of
    /// other values (FR-075). Each row is a list of values for the written columns, from the first, typed as the values
    /// of <see cref="SpreadsheetWorkbook.AddWorksheetAsync(SpreadsheetSheetDefinition, IEnumerable{IReadOnlyList{object}}, CancellationToken)"/>
    /// are; a shorter row ends with empty cells. The dropdown lists of choice columns stop above them. A reader reads
    /// footer rows as data rows, so a worksheet meant to be imported again has none.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>>? FooterRows { get; init; }
}
