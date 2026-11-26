using Elarion.Spreadsheets.Generated;

namespace Elarion.Spreadsheets;

/// <summary>
/// The mapping between a spreadsheet record and a worksheet row, generated per
/// <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> class as a sealed <c>{T}SpreadsheetRowMapper</c>
/// (ADR-0108). It holds the column table and the typed code that writes a record's values in column order; it is a
/// stateless singleton, reached through <see cref="ISpreadsheetRecord{TSelf}.SpreadsheetMapper"/>.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
public interface ISpreadsheetRowMapper<T> where T : class {
    /// <summary>The worksheet name the record declares, or its type name.</summary>
    string WorksheetName { get; }

    /// <summary>The record's columns, in column order; <see cref="SpreadsheetColumn{T}.Index"/> is the position.</summary>
    IReadOnlyList<SpreadsheetColumn<T>> Columns { get; }

    /// <summary>
    /// Writes the values of <paramref name="record"/> to <paramref name="writer"/>, one call per column in column order.
    /// Called by the workbook writer; application code does not call it.
    /// </summary>
    void WriteRow(T record, SpreadsheetCellWriter writer);
}
