using System.Diagnostics.CodeAnalysis;
using Elarion.Spreadsheets.Reading;

namespace Elarion.Spreadsheets;

/// <summary>
/// A workbook opened once by <see cref="SpreadsheetReader.OpenAsync"/>, readable as several worksheets and record types
/// without opening or parsing the file again (FR-069).
/// </summary>
/// <remarks>
/// A file that cannot be read at all — no workbook, damaged, password-protected, too large, without a visible worksheet
/// — opens all the same, with the one error in <see cref="FileErrors"/>; every read of it then returns that error and no
/// rows (FR-053, FR-067). Dispose the document when done. An instance is not thread-safe: read its worksheets one after
/// the other.
/// </remarks>
public sealed class SpreadsheetReadDocument : IDisposable {
    private readonly IWorkbookSource? _source;
    private readonly SpreadsheetReader _reader;
    private readonly SpreadsheetReadOptions _options;
    private bool _disposed;

    internal SpreadsheetReadDocument(
        SpreadsheetReader reader, SpreadsheetReadOptions options, IWorkbookSource? source, SpreadsheetError? fileError) {
        _reader = reader;
        _options = options;
        _source = source;
        if (fileError is null && source is { VisibleWorksheets.Count: 0 }) fileError = SpreadsheetErrors.NoVisibleWorksheet();

        FileErrors = fileError is null ? [] : [fileError];
        VisibleWorksheets = fileError is null && source is not null ? source.VisibleWorksheets : [];
    }

    /// <summary>
    /// The one error that keeps the whole file from being read (<see cref="SpreadsheetErrorCodes.Unreadable"/>,
    /// <see cref="SpreadsheetErrorCodes.TooLarge"/>, <see cref="SpreadsheetErrorCodes.NoVisibleWorksheet"/>), or none.
    /// </summary>
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }

    /// <summary>The names of the visible worksheets, in workbook order; empty when <see cref="FileErrors"/> has one.</summary>
    public IReadOnlyList<string> VisibleWorksheets { get; }

    /// <summary>
    /// Reads the selected worksheet — the first visible one unless <paramref name="selection"/> names one — as records of
    /// <typeparamref name="T"/>, with every row, every located error and the columns the file contains.
    /// </summary>
    /// <param name="selection">The worksheet and its header row; <see langword="null"/> takes the first visible worksheet and its first non-empty row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> carries validation attributes, but no request validator is registered or it holds no
    /// metadata for the record (FR-048).
    /// </exception>
    public async ValueTask<SpreadsheetReadResult<T>> ReadAsync<T>(
        SpreadsheetWorksheetSelection? selection = null, CancellationToken cancellationToken = default)
        where T : class, ISpreadsheetRecord<T> {
        RecordReader.EnsureValidationConfigured<T>(_reader.Validator);
        if (!TryOpenWorksheet(selection, out var source, out var worksheet, out var error))
            return new SpreadsheetReadResult<T>(error.Worksheet ?? (worksheet.Length > 0 ? worksheet : null), [], [error]);

        return await RecordReader.ReadAsync<T>(
            source, worksheet, selection, _options, _reader.Validator, _reader.NamingPolicy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the selected worksheet without a record (FR-058): its non-empty rows as typed cells with their row numbers,
    /// under the same worksheet choice, cell rules and row limit as a record read, plus the column limit (FR-059).
    /// </summary>
    /// <param name="selection">The worksheet; <see langword="null"/> takes the first visible one. Its header row is ignored.</param>
    public SpreadsheetRowsResult ReadRows(SpreadsheetWorksheetSelection? selection = null) {
        if (!TryOpenWorksheet(selection, out var source, out var worksheet, out var error))
            return new SpreadsheetRowsResult(error.Worksheet ?? (worksheet.Length > 0 ? worksheet : null), [], [error]);

        return PlainRowReader.Read(source, worksheet, _options);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (_disposed) return;

        _disposed = true;
        _source?.Dispose();
    }

    /// <summary>
    /// Opens the selected worksheet on the source, or returns the error that keeps it from being read: the file's own
    /// error, or a name no visible worksheet has.
    /// </summary>
    private bool TryOpenWorksheet(
        SpreadsheetWorksheetSelection? selection, out IWorkbookSource source, out string worksheet,
        [NotNullWhen(false)] out SpreadsheetError? error) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        source = _source!;
        worksheet = "";
        error = null;
        if (FileErrors.Count > 0) {
            error = FileErrors[0];
            return false;
        }

        if (selection?.Name is { } name) {
            var match = VisibleWorksheets.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
            if (match is null) {
                error = SpreadsheetErrors.WorksheetNotFound(name);
                return false;
            }

            worksheet = match;
        }
        else {
            worksheet = VisibleWorksheets[0];
        }

        try {
            if (source.OpenWorksheet(worksheet)) return true;

            error = SpreadsheetErrors.WorksheetNotFound(worksheet);
            return false;
        }
        catch (UnreadableWorkbookException) {
            error = SpreadsheetErrors.Unreadable("it is damaged or incomplete");
            return false;
        }
    }
}
