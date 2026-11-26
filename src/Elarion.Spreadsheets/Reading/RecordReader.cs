using System.Globalization;
using System.Text.Json;
using Elarion.Abstractions.Validation;
using Elarion.Spreadsheets.Generated;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets.Reading;

/// <summary>
/// Reads one opened worksheet as records (ADR-0108, decisions 4, 5 and 8): finds the header row, maps the record's
/// columns by header then alias, reads every data row through the generated mapper, and validates each error-free
/// record through the request validator.
/// </summary>
internal static class RecordReader {
    public static async ValueTask<SpreadsheetReadResult<T>> ReadAsync<T>(
        IWorkbookSource source,
        string worksheet,
        SpreadsheetWorksheetSelection? selection,
        SpreadsheetReadOptions options,
        IRequestValidator? validator,
        JsonNamingPolicy? namingPolicy,
        CancellationToken cancellationToken)
        where T : class, ISpreadsheetRecord<T> {
        var mapper = T.SpreadsheetMapper;
        var columns = mapper.Columns;
        var rows = new List<SpreadsheetRow<T>>();
        var fileErrors = new List<SpreadsheetError>();
        int[] ordinals;
        try {
            if (!FindHeaderRow(source, selection?.HeaderRow, out var headers)) headers = new Dictionary<string, List<int>>();

            ordinals = MapColumns(columns, headers, worksheet, fileErrors);
            if (fileErrors.Count > 0) return new SpreadsheetReadResult<T>(worksheet, [], fileErrors, Contained(columns, ordinals));

            var reader = new SpreadsheetCellReader(
                worksheet,
                [.. columns.Select(static column => column.Header)],
                [.. columns.Select(static column => column.Kind)],
                [.. columns.Select(static column => column.IsNullable)],
                [.. columns.Select(static column => column.Choices)],
                ordinals,
                options);
            var dataRows = 0;
            while (source.ReadRow()) {
                if ((dataRows & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();

                var cells = new SpreadsheetCell[columns.Count];
                var empty = true;
                for (var i = 0; i < cells.Length; i++) {
                    if (ordinals[i] < 0) continue;

                    cells[i] = source.GetCell(ordinals[i]);
                    empty &= IsEmpty(cells[i]);
                }

                // Empty rows produce nothing and do not count against the limit; the others keep their own number (FR-033).
                if (empty) continue;

                if (++dataRows > options.MaxRows) {
                    fileErrors.Add(SpreadsheetErrors.TooManyRows(worksheet, options.MaxRows, source.RowNumber));
                    break;
                }

                var errors = new List<SpreadsheetError>();
                reader.BeginRow(source.RowNumber, cells, errors);
                var record = mapper.ReadRow(reader);
                rows.Add(new SpreadsheetRow<T>(worksheet, source.RowNumber, record, cells, ordinals, errors));
            }
        }
        catch (UnreadableWorkbookException) {
            return new SpreadsheetReadResult<T>(worksheet, [], [SpreadsheetErrors.Unreadable("it is damaged or incomplete")]);
        }

        if (mapper.HasValidation && validator is not null)
            await ValidateAsync(rows, columns, ordinals, validator, namingPolicy, cancellationToken).ConfigureAwait(false);

        return new SpreadsheetReadResult<T>(worksheet, rows, fileErrors, Contained(columns, ordinals));
    }

    /// <summary>
    /// Throws when <typeparamref name="T"/> carries validation attributes that nothing would check (FR-048): no request
    /// validator is registered, or it holds no metadata for the record because its module is disabled.
    /// </summary>
    public static void EnsureValidationConfigured<T>(IRequestValidator? validator) where T : class, ISpreadsheetRecord<T> {
        if (!T.SpreadsheetMapper.HasValidation) return;

        var name = typeof(T).FullName;
        if (validator is null)
            throw new InvalidOperationException(
                $"The spreadsheet record '{name}' carries validation attributes, but no IRequestValidator is registered, so its "
                + "rows would not be validated. Reference Elarion.Validation and call AddElarionValidation().");

        if (!validator.HasMetadata(typeof(T)))
            throw new InvalidOperationException(
                $"The spreadsheet record '{name}' carries validation attributes, but the request validator holds no validation "
                + "metadata for it, so its rows would not be validated. Its module is disabled, or the record lies outside the "
                + "namespace of every module of an assembly marked [assembly: UseElarion].");
    }

    /// <summary>
    /// Advances to the header row — the given row number, or the first non-empty row — and returns its trimmed headers
    /// with their column ordinals.
    /// </summary>
    private static bool FindHeaderRow(IWorkbookSource source, int? headerRow, out Dictionary<string, List<int>> headers) {
        headers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        while (source.ReadRow()) {
            if (headerRow is { } wanted) {
                if (source.RowNumber < wanted) continue;
                if (source.RowNumber > wanted) return false;
            }

            for (var ordinal = 0; ordinal < source.CellCount; ordinal++) {
                var text = source.GetCell(ordinal).Text.Trim();
                if (text.Length == 0) continue;

                if (!headers.TryGetValue(text, out var list)) headers[text] = list = [];
                list.Add(ordinal);
            }

            if (headers.Count > 0 || headerRow is not null) return true;
        }

        return false;
    }

    /// <summary>
    /// The file column of each record column: its header, else its first alias the file carries (FR-005, FR-031), or −1.
    /// A mapped header carried twice is <c>column_duplicated</c> (FR-032); a missing column whose property is not nullable
    /// is <c>column_missing</c> (FR-036). A column the reader never sets is neither.
    /// </summary>
    private static int[] MapColumns<T>(
        IReadOnlyList<SpreadsheetColumn<T>> columns, Dictionary<string, List<int>> headers, string worksheet,
        List<SpreadsheetError> errors)
        where T : class {
        var ordinals = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++) {
            var column = columns[i];
            ordinals[i] = -1;
            foreach (var name in new[] { column.Header }.Concat(column.Aliases)) {
                if (!headers.TryGetValue(name.Trim(), out var found)) continue;

                if (found.Count > 1 && column.IsReadable)
                    errors.Add(new SpreadsheetError(
                        SpreadsheetErrorCodes.ColumnDuplicated, $"The column '{name.Trim()}' appears more than once.", worksheet,
                        header: column.Header));

                ordinals[i] = found[0];
                break;
            }

            if (ordinals[i] < 0 && column.IsReadable && !column.IsNullable)
                errors.Add(new SpreadsheetError(
                    SpreadsheetErrorCodes.ColumnMissing, $"The column '{column.Header}' is missing.", worksheet, header: column.Header));
        }

        return ordinals;
    }

    private static IEnumerable<SpreadsheetColumn<T>> Contained<T>(IReadOnlyList<SpreadsheetColumn<T>> columns, int[] ordinals)
        where T : class {
        return columns.Where((_, i) => ordinals[i] >= 0);
    }

    private static bool IsEmpty(SpreadsheetCell cell) {
        return cell.Kind == SpreadsheetCellKind.Blank
               || (cell.Kind == SpreadsheetCellKind.Text && string.IsNullOrWhiteSpace(cell.Text));
    }

    /// <summary>
    /// Validates each error-free record (FR-047) and turns the violations into <c>validation</c> errors located by
    /// column: a wire-named path's first segment is matched against the property names under the canonical naming
    /// policy, and a path that names no column becomes an error of the whole row.
    /// </summary>
    private static async ValueTask ValidateAsync<T>(
        List<SpreadsheetRow<T>> rows,
        IReadOnlyList<SpreadsheetColumn<T>> columns,
        int[] ordinals,
        IRequestValidator validator,
        JsonNamingPolicy? namingPolicy,
        CancellationToken cancellationToken)
        where T : class {
        var byWireName = new Dictionary<string, int>(StringComparer.Ordinal);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) {
            byWireName.TryAdd(namingPolicy?.ConvertName(columns[i].PropertyName) ?? columns[i].PropertyName, i);
            byName.TryAdd(columns[i].PropertyName, i);
        }

        foreach (var row in rows) {
            if (!row.IsValid) continue;

            var violations = await validator.ValidateAsync(typeof(T), row.Record, cancellationToken).ConfigureAwait(false);
            var errors = new List<SpreadsheetError>();
            if (violations is null) continue;

            foreach (var (path, messages) in violations.FieldErrors.OrderBy(pair => ColumnOf(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            foreach (var message in messages) {
                var index = ColumnOf(path);
                var header = index < columns.Count ? columns[index].Header : null;
                var cell = index < columns.Count && ordinals[index] >= 0
                    ? SpreadsheetCells.ColumnLetters(ordinals[index]) + row.RowNumber.ToString(CultureInfo.InvariantCulture)
                    : null;
                errors.Add(new SpreadsheetError(SpreadsheetErrorCodes.Validation, message, row.Worksheet, row.RowNumber, header, cell));
            }

            foreach (var error in errors) row.Reject(error);
        }

        int ColumnOf(string path) {
            var end = path.AsSpan().IndexOfAny('.', '[');
            var name = end < 0 ? path : path[..end];
            return byWireName.TryGetValue(name, out var index) || byName.TryGetValue(name, out index) ? index : int.MaxValue;
        }
    }
}
