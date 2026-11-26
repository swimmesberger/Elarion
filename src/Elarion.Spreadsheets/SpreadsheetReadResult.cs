using System.Globalization;
using Elarion.Abstractions;

namespace Elarion.Spreadsheets;

/// <summary>
/// The result of reading a worksheet as records (ADR-0108, decision 4): every data row with its record or its errors,
/// the errors about the file and its columns, and which columns the file contains — so a handler builds a preview, and
/// an update that leaves alone what the file does not state.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>The constructor is public so a handler's tests can build a result by hand.</remarks>
/// <example>
/// <code>
/// var result = await reader.ReadAsync&lt;ContactRow&gt;(request.Workbook.OpenReadStream(), cancellationToken: ct);
/// if (result.HasErrors) return result.ToAppError("workbook");
/// foreach (var contact in result.Records) {
///     var stored = await db.Contacts.FindAsync([contact.Key], ct);
///     if (result.Contains(ContactRow.Columns.Phone)) stored.Phone = contact.Phone;   // an empty cell clears it
/// }
/// </code>
/// </example>
public sealed class SpreadsheetReadResult<T> where T : class {
    /// <summary>The default of <see cref="ToAppError"/>'s message cap: 100.</summary>
    public const int DefaultMaxMessages = 100;

    private readonly HashSet<int> _contained;

    /// <summary>Creates a result.</summary>
    /// <param name="worksheet">The worksheet read, or <see langword="null"/> when none could be.</param>
    /// <param name="rows">The data rows, in row order.</param>
    /// <param name="fileErrors">The errors about the file and its columns.</param>
    /// <param name="containedColumns">The record's columns the file contains.</param>
    public SpreadsheetReadResult(
        string? worksheet,
        IReadOnlyList<SpreadsheetRow<T>> rows,
        IReadOnlyList<SpreadsheetError>? fileErrors = null,
        IEnumerable<SpreadsheetColumn<T>>? containedColumns = null) {
        ArgumentNullException.ThrowIfNull(rows);
        Worksheet = worksheet;
        Rows = rows;
        FileErrors = fileErrors ?? [];
        _contained = containedColumns is null ? [] : [.. containedColumns.Select(static column => column.Index)];
    }

    /// <summary>The worksheet read, or <see langword="null"/> when none could be.</summary>
    public string? Worksheet { get; }

    /// <summary>Every data row read, with or without a record (FR-034); empty when a file or column error stops the read.</summary>
    public IReadOnlyList<SpreadsheetRow<T>> Rows { get; }

    /// <summary>
    /// The errors about the file (unreadable, too large, no such worksheet, too many rows) and its columns (missing,
    /// duplicated).
    /// </summary>
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }

    /// <summary>Every error: the file errors, then each row's in row order, including those the handler added (FR-052).</summary>
    public IEnumerable<SpreadsheetError> Errors => FileErrors.Concat(Rows.SelectMany(static row => row.Errors));

    /// <summary>The records of the rows without errors.</summary>
    public IEnumerable<T> Records => Rows.Where(static row => row.IsValid).Select(static row => row.Record!);

    /// <summary>Whether the result has any error, including one the handler added.</summary>
    public bool HasErrors => FileErrors.Count > 0 || Rows.Any(static row => row.Errors.Count > 0);

    /// <summary>
    /// Whether the file contains <paramref name="column"/>, by its header or an alias (FR-035). A column the file lacks
    /// reads as <see langword="null"/> without an error when its property is nullable; this tells that apart from an
    /// empty cell, so an update leaves an unstated value alone and clears a stated empty one.
    /// </summary>
    public bool Contains(SpreadsheetColumn<T> column) {
        ArgumentNullException.ThrowIfNull(column);
        return _contained.Contains(column.Index);
    }

    /// <summary>
    /// Converts the errors to an <see cref="ErrorKind.Validation"/> <see cref="AppError"/> whose field errors are keyed by
    /// <paramref name="fieldPath"/> (FR-054): normally the uploaded-file member, such as <c>workbook</c>.
    /// </summary>
    /// <param name="fieldPath">The wire-named field path of the upload, or <see langword="null"/> for the empty key.</param>
    /// <param name="format">
    /// Words one error, for example in the application's language from <see cref="SpreadsheetError.Code"/> and its data;
    /// <see langword="null"/> uses <see cref="SpreadsheetError.ToString"/>, which names the row, the column and the cell.
    /// </param>
    /// <param name="maxMessages">
    /// The most messages; the rest are counted in one closing message, so a broken large file does not become a
    /// multi-megabyte response.
    /// </param>
    /// <exception cref="InvalidOperationException">The result has no error.</exception>
    public AppError ToAppError(string? fieldPath = null, Func<SpreadsheetError, string>? format = null, int maxMessages = DefaultMaxMessages) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        var errors = Errors.ToList();
        if (errors.Count == 0) throw new InvalidOperationException("The spreadsheet read result has no error to convert.");

        format ??= static error => error.ToString();
        var messages = errors.Take(maxMessages).Select(format).ToList();
        if (errors.Count > maxMessages) {
            var rest = errors.Count - maxMessages;
            messages.Add(rest == 1 ? "1 more problem is not listed." : $"{rest.ToString(CultureInfo.InvariantCulture)} more problems are not listed.");
        }

        var count = errors.Count == 1 ? "1 problem" : $"{errors.Count.ToString(CultureInfo.InvariantCulture)} problems";
        return AppError.Validation(
            $"The workbook has {count}.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [fieldPath ?? ""] = [.. messages] });
    }
}
