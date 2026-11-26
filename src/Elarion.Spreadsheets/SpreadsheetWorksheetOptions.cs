namespace Elarion.Spreadsheets;

/// <summary>Options of one worksheet added with <see cref="SpreadsheetWorkbook.AddWorksheetAsync{T}(IEnumerable{T}, SpreadsheetWorksheetOptions?, CancellationToken)"/>.</summary>
public sealed record SpreadsheetWorksheetOptions {
    /// <summary>
    /// The worksheet's name; <see langword="null"/> uses the name the record declares. It follows the rules of
    /// <see cref="SpreadsheetRecordAttribute.WorksheetName"/>, checked when the worksheet is added.
    /// </summary>
    public string? Name { get; init; }
}
