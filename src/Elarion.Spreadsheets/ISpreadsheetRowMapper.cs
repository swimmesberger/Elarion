using Elarion.Spreadsheets.Generated;

namespace Elarion.Spreadsheets;

/// <summary>
/// The mapping between a spreadsheet record and a worksheet row, generated per
/// <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> class as a sealed <c>{T}SpreadsheetRowMapper</c>
/// (ADR-0108). It holds the column table, the typed code that writes a record's values in column order, and the typed
/// code that reads a row back into a record; it is a stateless singleton, reached through
/// <see cref="ISpreadsheetRecord{TSelf}.SpreadsheetMapper"/>.
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

    /// <summary>
    /// Whether the record carries DataAnnotations or implements <c>IValidatableObject</c>, so that each record read is
    /// validated through the request validator (FR-047) and a read without validation metadata fails (FR-048).
    /// </summary>
    bool HasValidation { get; }

    /// <summary>
    /// Reads the current row of <paramref name="reader"/> into a record, one typed read per readable column, or returns
    /// <see langword="null"/> when a cell has an error, which the reader then holds. A column whose property cannot be set
    /// is not read (FR-042). Called by <see cref="SpreadsheetReader"/>; application code does not call it.
    /// </summary>
    T? ReadRow(SpreadsheetCellReader reader);
}
