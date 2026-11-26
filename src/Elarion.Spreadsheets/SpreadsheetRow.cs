using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets;

/// <summary>
/// One data row of a record read (ADR-0108, decision 4): the row number the spreadsheet shows, the text of each mapped
/// cell, the row's errors, and the record when the row has none — so a preview lists every row, valid or not (FR-034).
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>
/// <see cref="AddError"/> is the one mutation: the handler adds its own rule errors (an unknown key, a duplicate) before
/// it builds its preview or <see cref="SpreadsheetReadResult{T}.ToAppError"/>. A row is not thread-safe.
/// </remarks>
public sealed class SpreadsheetRow<T> where T : class {
    private readonly SpreadsheetCell[] _cells;
    private readonly int[]? _ordinals;
    private readonly List<SpreadsheetError> _errors;

    /// <summary>
    /// Creates a row by hand, for a handler's tests. Cell references are derived from the column positions, as in a
    /// file the package wrote.
    /// </summary>
    /// <param name="rowNumber">The row number the spreadsheet shows.</param>
    /// <param name="record">The record, or <see langword="null"/> for a row with errors.</param>
    /// <param name="cellTexts">The text of each column's cell, by <see cref="SpreadsheetColumn{T}.Index"/>.</param>
    /// <param name="errors">The row's errors.</param>
    /// <param name="worksheet">The worksheet's name.</param>
    public SpreadsheetRow(
        int rowNumber,
        T? record,
        IReadOnlyList<string>? cellTexts = null,
        IEnumerable<SpreadsheetError>? errors = null,
        string? worksheet = null) {
        RowNumber = rowNumber;
        Record = record;
        Worksheet = worksheet;
        _cells = cellTexts is null ? [] : [.. cellTexts.Select(SpreadsheetCell.FromText)];
        _errors = errors is null ? [] : [.. errors];
    }

    internal SpreadsheetRow(string worksheet, int rowNumber, T? record, SpreadsheetCell[] cells, int[] ordinals, List<SpreadsheetError> errors) {
        Worksheet = worksheet;
        RowNumber = rowNumber;
        Record = record;
        _cells = cells;
        _ordinals = ordinals;
        _errors = errors;
    }

    /// <summary>The worksheet the row belongs to.</summary>
    public string? Worksheet { get; }

    /// <summary>The row number the spreadsheet shows (FR-033).</summary>
    public int RowNumber { get; }

    /// <summary>
    /// The record, when the package found no error in the row (a cell that does not convert, or a validation rule);
    /// errors the handler adds later leave it in place, but make <see cref="IsValid"/> false.
    /// </summary>
    public T? Record { get; private set; }

    /// <summary>Whether the row has a record and no error, its own or one the handler added.</summary>
    [MemberNotNullWhen(true, nameof(Record))]
    public bool IsValid => Record is not null && _errors.Count == 0;

    /// <summary>The row's errors, in column order, then the ones the handler added.</summary>
    public IReadOnlyList<SpreadsheetError> Errors => _errors;

    /// <summary>
    /// The text of the row's cell in <paramref name="column"/>, as FR-037 defines it; empty for a column the file lacks,
    /// so a preview shows what the person typed even where it did not convert.
    /// </summary>
    public string CellText(SpreadsheetColumn<T> column) {
        ArgumentNullException.ThrowIfNull(column);
        return column.Index < _cells.Length ? _cells[column.Index].Text : "";
    }

    /// <summary>Adds a validation error found by the package: the row then holds no record.</summary>
    internal void Reject(SpreadsheetError error) {
        _errors.Add(error);
        Record = null;
    }

    /// <summary>
    /// Adds the application's own error to the row (FR-068), located like the package's: the worksheet, the row, and for
    /// a <paramref name="column"/> its header and cell reference. The row stops being valid, and the error appears in
    /// <see cref="SpreadsheetReadResult{T}.Errors"/> and its <c>AppError</c>.
    /// </summary>
    /// <param name="column">The column the error is about, or <see langword="null"/> for the whole row.</param>
    /// <param name="code">The application's stable code, for example <c>contacts.unknown_key</c>.</param>
    /// <param name="message">The message, without the location.</param>
    /// <example>
    /// <code>
    /// foreach (var row in result.Rows.Where(row => row.IsValid))
    ///     if (!knownKeys.Contains(row.Record!.Key))
    ///         row.AddError(ContactRow.Columns.Key, "contacts.unknown_key", "No contact has this key.");
    /// </code>
    /// </example>
    public void AddError(SpreadsheetColumn<T>? column, string code, string message) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        string? cell = null;
        if (column is not null) {
            var ordinal = _ordinals is null ? column.Index : column.Index < _ordinals.Length ? _ordinals[column.Index] : -1;
            if (ordinal >= 0) cell = SpreadsheetCells.ColumnLetters(ordinal) + RowNumber.ToString(CultureInfo.InvariantCulture);
        }

        _errors.Add(new SpreadsheetError(code, message, Worksheet, RowNumber, column?.Header, cell));
    }
}
