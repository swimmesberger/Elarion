using System.IO.Compression;
using Elarion.Abstractions;
using Elarion.Spreadsheets.Generated;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets;

/// <summary>
/// Writes spreadsheet records as an <c>.xlsx</c> workbook, forward-only to a stream (ADR-0108): one worksheet per
/// <see cref="AddWorksheetAsync{T}(IEnumerable{T}, SpreadsheetWorksheetOptions?, CancellationToken)"/> call, in the
/// order written, each with a bold, filled, bordered and frozen header row, then one row per record. A worksheet whose
/// columns are known only at run time is a <see cref="SpreadsheetSheetDefinition"/>, written the same way from rows of
/// values.
/// </summary>
/// <remarks>
/// <para>
/// The writer holds only the first <see cref="SpreadsheetWriteOptions.WidthSampleRows"/> records of a worksheet, which
/// it measures for the column widths; every other record streams to the destination as it comes (FR-023). The
/// workbook's remaining parts are written when it is disposed, so always dispose it (<c>await using</c>). The
/// destination stays open and need not be seekable.
/// </para>
/// <para>
/// The same records give byte-identical files with the same package version on the same runtime and platform: entries
/// carry a fixed timestamp and the workbook has no document properties (FR-025). A value a spreadsheet cannot hold fails
/// with a <see cref="SpreadsheetWriteException"/>; the workbook is then incomplete and no further worksheet can be
/// added. An instance is not thread-safe.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using (var workbook = SpreadsheetWorkbook.Create(stream)) {
///     await workbook.AddWorksheetAsync(contacts, cancellationToken: ct);
///     await workbook.AddWorksheetAsync(bankAccounts, cancellationToken: ct);
/// }
/// </code>
/// </example>
public sealed class SpreadsheetWorkbook : IAsyncDisposable {
    /// <summary>The content type of an <c>.xlsx</c> workbook.</summary>
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // ZIP stores local wall-clock times from 1980 on; a fixed one keeps the output deterministic (FR-025).
    private static readonly DateTimeOffset EntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Stream _destination;
    private readonly SpreadsheetWriteOptions _options;
    private readonly SpreadsheetStyles _styles = new();
    private readonly SpreadsheetDropdowns _dropdowns = new();
    private readonly List<string> _worksheets = [];
    private readonly HashSet<string> _worksheetNames = new(StringComparer.OrdinalIgnoreCase);

    private ZipArchive? _archive;
    private bool _faulted;
    private bool _disposed;

    private SpreadsheetWorkbook(Stream destination, SpreadsheetWriteOptions options) {
        _destination = destination;
        _options = options;
    }

    /// <summary>
    /// Starts a workbook that writes to <paramref name="destination"/>. Nothing is written until the first worksheet is
    /// added.
    /// </summary>
    /// <param name="destination">A writable stream; it stays open, and need not be seekable.</param>
    /// <param name="options">The write options; <see langword="null"/> uses <see cref="SpreadsheetWriteOptions.Default"/>.</param>
    public static SpreadsheetWorkbook Create(Stream destination, SpreadsheetWriteOptions? options = null) {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(destination));

        return new SpreadsheetWorkbook(destination, ValidateOptions(options));
    }

    /// <summary>Writes <paramref name="records"/> as a workbook with one worksheet to <paramref name="destination"/>.</summary>
    /// <param name="destination">A writable stream; it stays open.</param>
    /// <param name="records">The records, one row each.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask WriteAsync<T>(
        Stream destination,
        IEnumerable<T> records,
        SpreadsheetWriteOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        var workbook = Create(destination, options);
        await using (workbook.ConfigureAwait(false)) {
            await workbook.AddWorksheetAsync(records, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes <paramref name="records"/> as a workbook with one worksheet to <paramref name="destination"/>.</summary>
    /// <param name="destination">A writable stream; it stays open.</param>
    /// <param name="records">The records, one row each, produced one at a time.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask WriteAsync<T>(
        Stream destination,
        IAsyncEnumerable<T> records,
        SpreadsheetWriteOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        var workbook = Create(destination, options);
        await using (workbook.ConfigureAwait(false)) {
            await workbook.AddWorksheetAsync(records, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <paramref name="records"/> as a workbook with one worksheet into memory and returns it as a file with the
    /// <c>.xlsx</c> content type, for a handler that returns a small export to any transport.
    /// </summary>
    /// <param name="records">The records, one row each.</param>
    /// <param name="fileName">The name the caller saves the file with, such as <c>contacts.xlsx</c>.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The whole workbook is held in memory, as an <see cref="ElarionFile"/> always is; for a large export return
    /// <see cref="ToStreamedFile{T}"/> from an HTTP endpoint instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await SpreadsheetWorkbook.ToFileAsync(contacts, "contacts.xlsx", cancellationToken: ct);
    /// </code>
    /// </example>
    public static async ValueTask<ElarionFile> ToFileAsync<T>(
        IEnumerable<T> records,
        string fileName,
        SpreadsheetWriteOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        using var stream = new MemoryStream();
        await WriteAsync(stream, records, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// Writes <paramref name="records"/> as a workbook with one worksheet into memory and returns it as a file with the
    /// <c>.xlsx</c> content type, for a handler that returns a small export to any transport.
    /// </summary>
    /// <param name="records">The records, one row each, produced one at a time.</param>
    /// <param name="fileName">The name the caller saves the file with, such as <c>contacts.xlsx</c>.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The whole workbook is held in memory, as an <see cref="ElarionFile"/> always is; for a large export return
    /// <see cref="ToStreamedFile{T}"/> from an HTTP endpoint instead.
    /// </remarks>
    public static async ValueTask<ElarionFile> ToFileAsync<T>(
        IAsyncEnumerable<T> records,
        string fileName,
        SpreadsheetWriteOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        using var stream = new MemoryStream();
        await WriteAsync(stream, records, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// Writes a workbook that <paramref name="write"/> fills with any number of worksheets into memory and returns it as a
    /// file with the <c>.xlsx</c> content type, for a handler that returns a small export to any transport.
    /// </summary>
    /// <param name="fileName">The name the caller saves the file with, such as <c>customers.xlsx</c>.</param>
    /// <param name="write">Adds the worksheets, at least one. The workbook is completed after it returns; do not dispose it.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <example>
    /// <code>
    /// return await SpreadsheetWorkbook.ToFileAsync("customers.xlsx", async (workbook, ct) => {
    ///     await workbook.AddWorksheetAsync(sheet, rows, ct);
    ///     await workbook.AddWorksheetAsync(bankAccounts, cancellationToken: ct);
    /// }, cancellationToken: ct);
    /// </code>
    /// </example>
    public static async ValueTask<ElarionFile> ToFileAsync(
        string fileName,
        Func<SpreadsheetWorkbook, CancellationToken, ValueTask> write,
        SpreadsheetWriteOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(write);

        using var stream = new MemoryStream();
        var workbook = Create(stream, options);
        try {
            await write(workbook, cancellationToken).ConfigureAwait(false);
        }
        catch {
            workbook._faulted = true;
            await workbook.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await workbook.DisposeAsync().ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// A streamed file with the <c>.xlsx</c> content type whose workbook holds <paramref name="records"/> in one
    /// worksheet, written to the response body as the records come, for an HTTP endpoint that exports more than fits a
    /// buffered <see cref="ElarionFile"/>.
    /// </summary>
    /// <param name="records">
    /// The records, enumerated once when the response is written: after the handler's pipeline and transaction, so they
    /// must not depend on a scope that has ended by then.
    /// </param>
    /// <param name="fileName">The name the client saves the file with, such as <c>contacts.xlsx</c>.</param>
    /// <param name="options">The write options.</param>
    /// <example>
    /// <code>
    /// return SpreadsheetWorkbook.ToStreamedFile(db.Contacts.AsNoTracking().Select(…).AsAsyncEnumerable(), "contacts.xlsx");
    /// </code>
    /// </example>
    public static ElarionStreamedFile ToStreamedFile<T>(
        IAsyncEnumerable<T> records,
        string fileName,
        SpreadsheetWriteOptions? options = null) where T : class, ISpreadsheetRecord<T> {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        options = ValidateOptions(options);

        return ElarionStreamedFile.FromWriter(
            (body, cancellationToken) => WriteAsync(body, records, options, cancellationToken).AsTask(),
            ContentType) with { FileName = fileName };
    }

    /// <summary>
    /// A streamed file with the <c>.xlsx</c> content type whose workbook <paramref name="write"/> fills with any number
    /// of worksheets, written to the response body as they come.
    /// </summary>
    /// <param name="fileName">The name the client saves the file with, such as <c>customers.xlsx</c>.</param>
    /// <param name="write">
    /// Adds the worksheets; it runs when the response is written, after the handler's pipeline and transaction, and
    /// must add at least one. The workbook is completed after it returns; do not dispose it.
    /// </param>
    /// <param name="options">The write options.</param>
    /// <example>
    /// <code>
    /// return SpreadsheetWorkbook.ToStreamedFile("customers.xlsx", async (workbook, ct) => {
    ///     await workbook.AddWorksheetAsync(contacts, cancellationToken: ct);
    ///     await workbook.AddWorksheetAsync(bankAccounts, cancellationToken: ct);
    /// });
    /// </code>
    /// </example>
    public static ElarionStreamedFile ToStreamedFile(
        string fileName,
        Func<SpreadsheetWorkbook, CancellationToken, ValueTask> write,
        SpreadsheetWriteOptions? options = null) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(write);
        options = ValidateOptions(options);

        return ElarionStreamedFile.FromWriter(
            async (body, cancellationToken) => {
                var workbook = new SpreadsheetWorkbook(body, options);
                try {
                    await write(workbook, cancellationToken).ConfigureAwait(false);
                }
                catch {
                    // Close what was written without the dispose's "no worksheet" error hiding the writer's own.
                    workbook._faulted = true;
                    await workbook.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                await workbook.DisposeAsync().ConfigureAwait(false);
            },
            ContentType) with { FileName = fileName };
    }

    /// <summary>Adds a worksheet holding <paramref name="records"/>, one row each, after the worksheets already added.</summary>
    /// <param name="records">The records.</param>
    /// <param name="options">The worksheet's options; <see langword="null"/> uses the name the record declares.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="SpreadsheetWriteException">
    /// A value cannot be written, the worksheet name is invalid, or another worksheet of the workbook has the same name
    /// ignoring case.
    /// </exception>
    public ValueTask AddWorksheetAsync<T>(
        IEnumerable<T> records,
        SpreadsheetWorksheetOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        ArgumentNullException.ThrowIfNull(records);
        return AddWorksheetCoreAsync(new SynchronousRecords<T>(records), options, cancellationToken);
    }

    /// <summary>Adds a worksheet holding <paramref name="records"/>, one row each, after the worksheets already added.</summary>
    /// <param name="records">The records, produced one at a time; they stream to the destination as they come.</param>
    /// <param name="options">The worksheet's options; <see langword="null"/> uses the name the record declares.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="SpreadsheetWriteException">
    /// A value cannot be written, the worksheet name is invalid, or another worksheet of the workbook has the same name
    /// ignoring case.
    /// </exception>
    public ValueTask AddWorksheetAsync<T>(
        IAsyncEnumerable<T> records,
        SpreadsheetWorksheetOptions? options = null,
        CancellationToken cancellationToken = default) where T : class, ISpreadsheetRecord<T> {
        ArgumentNullException.ThrowIfNull(records);
        return AddWorksheetCoreAsync(records, options, cancellationToken);
    }

    /// <summary>
    /// Adds a worksheet whose columns are known only at run time (FR-073): <paramref name="sheet"/> names it and declares
    /// its columns, and each row is a list of values in column order, written as a record's would be.
    /// </summary>
    /// <param name="sheet">The worksheet's name and columns.</param>
    /// <param name="rows">
    /// The rows, one list of values each. A value's type decides its cell — text, a boolean, a number, a date, a time, a
    /// <see cref="DateTimeOffset"/>, a <see cref="Guid"/>, an enum (its name), or <see langword="null"/> for an empty cell
    /// — and the column's format applies when the value is of the column's kind, so a row may hold a label such as
    /// "Total" in a date column, or a second block of other types below the first (FR-074). A row shorter than the
    /// columns ends with empty cells.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="SpreadsheetWriteException">
    /// A value cannot be written or has a type no cell holds, a row has more values than the worksheet has columns, or
    /// another worksheet of the workbook has the same name ignoring case.
    /// </exception>
    /// <example>
    /// <code>
    /// var sheet = new SpreadsheetSheetDefinition("Contacts", [
    ///     SpreadsheetColumnDefinition.Text("Name"),
    ///     .. Enumerable.Range(1, 4).Select(slot => SpreadsheetColumnDefinition.Choice($"E-mail {slot} label", labels)),
    ///     SpreadsheetColumnDefinition.Number("Area", format: "#,##0.00"),
    /// ]);
    /// await workbook.AddWorksheetAsync(sheet, contacts.Select(contact => contact.ToRow()), ct);
    /// </code>
    /// </example>
    public ValueTask AddWorksheetAsync(
        SpreadsheetSheetDefinition sheet,
        IEnumerable<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(rows);
        return AddDefinedWorksheetCoreAsync(sheet, new SynchronousRecords<IReadOnlyList<object?>>(rows), cancellationToken);
    }

    /// <summary>
    /// Adds a worksheet whose columns are known only at run time (FR-073), its rows produced one at a time; they stream
    /// to the destination as they come.
    /// </summary>
    /// <param name="sheet">The worksheet's name and columns.</param>
    /// <param name="rows">The rows, one list of values each, as for the synchronous overload.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="SpreadsheetWriteException">
    /// A value cannot be written or has a type no cell holds, a row has more values than the worksheet has columns, or
    /// another worksheet of the workbook has the same name ignoring case.
    /// </exception>
    public ValueTask AddWorksheetAsync(
        SpreadsheetSheetDefinition sheet,
        IAsyncEnumerable<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(rows);
        return AddDefinedWorksheetCoreAsync(sheet, rows, cancellationToken);
    }

    /// <summary>
    /// Writes the workbook's remaining parts and completes the archive. A workbook to which no worksheet was added fails
    /// with an <see cref="InvalidOperationException"/>, because a workbook needs at least one worksheet; a workbook whose
    /// write failed is only closed.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (_disposed) return;

        _disposed = true;
        if (_archive is null) {
            if (!_faulted)
                throw new InvalidOperationException("A workbook needs at least one worksheet; add one before disposing it.");

            return;
        }

        try {
            // The closing parts are small and must complete for the file to be a workbook at all, so they are written
            // without the token of a worksheet call that has already returned.
            if (!_faulted) await WritePackagePartsAsync(_archive, CancellationToken.None).ConfigureAwait(false);
        }
        finally {
            await _archive.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SpreadsheetWriteOptions ValidateOptions(SpreadsheetWriteOptions? options) {
        options ??= SpreadsheetWriteOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.WidthSampleRows, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.DropdownRows, nameof(options));
        if (!(options.MaxColumnWidth >= 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxColumnWidth, "MaxColumnWidth must be one or more.");

        return options;
    }

    // The buffer itself, not a copy: the stream is not used again.
    private static ElarionFile ToFile(MemoryStream stream, string fileName) {
        return new ElarionFile(stream.GetBuffer().AsMemory(0, (int)stream.Length), ContentType) { FileName = fileName };
    }

    private async ValueTask AddWorksheetCoreAsync<T>(
        IAsyncEnumerable<T> records,
        SpreadsheetWorksheetOptions? options,
        CancellationToken cancellationToken) where T : class, ISpreadsheetRecord<T> {
        EnsureWritable();
        var mapper = T.SpreadsheetMapper;
        var (columns, map) = SelectColumns(mapper.Columns, options);
        var footerRows = options?.FooterRows ?? [];
        if (footerRows.Any(static row => row is null))
            throw new ArgumentException("A footer row must not be null.", nameof(options));

        var name = options?.Name ?? mapper.WorksheetName;
        try {
            await WriteWorksheetAsync(
                name, columns, map, records,
                (cells, record, rowNumber) => {
                    if (record is null)
                        throw new SpreadsheetWriteException(
                            $"Cannot write row {rowNumber} of worksheet '{name}': the record is null.", name, rowNumber);

                    mapper.WriteRow(record, cells);
                },
                footerRows, cancellationToken).ConfigureAwait(false);
        }
        catch {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask AddDefinedWorksheetCoreAsync(
        SpreadsheetSheetDefinition sheet,
        IAsyncEnumerable<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken) {
        EnsureWritable();
        var columns = sheet.Columns
            .Select(static column => new WorksheetColumn(column.Header, column.Kind, column.Format, column.Width, column.Choices))
            .ToArray();
        try {
            await WriteWorksheetAsync<IReadOnlyList<object?>>(
                sheet.Name, columns, null, rows,
                (cells, row, rowNumber) => {
                    if (row is null)
                        throw new SpreadsheetWriteException(
                            $"Cannot write row {rowNumber} of worksheet '{sheet.Name}': the row is null.", sheet.Name, rowNumber);

                    cells.WriteValues(row);
                },
                [], cancellationToken).ConfigureAwait(false);
        }
        catch {
            _faulted = true;
            throw;
        }
    }

    private void EnsureWritable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("An earlier write of this workbook failed; it is incomplete and takes no further worksheets.");
    }

    /// <summary>
    /// The columns a record's worksheet writes, after <see cref="SpreadsheetWorksheetOptions.IncludedColumns"/> and
    /// <see cref="SpreadsheetWorksheetOptions.ExcludedColumns"/>, and the worksheet column of each record column (-1 for
    /// one left out); no map when every column is written.
    /// </summary>
    private static (WorksheetColumn[] Columns, int[]? Map) SelectColumns<T>(
        IReadOnlyList<SpreadsheetColumn<T>> all, SpreadsheetWorksheetOptions? options) where T : class {
        var included = options?.IncludedColumns;
        var excluded = options?.ExcludedColumns;
        if (included is null && excluded is null)
            return ([.. all.Select(ToWorksheetColumn)], null);

        // A name no column has is a mistake, not a filter that happens to match nothing.
        var unknown = (included ?? []).Concat(excluded ?? [])
            .Where(name => !all.Any(column => string.Equals(column.PropertyName, name, StringComparison.Ordinal)))
            .ToList();
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"The record '{typeof(T).Name}' has no column for the propert{(unknown.Count == 1 ? "y" : "ies")} "
                + $"{string.Join(", ", unknown.Select(static name => $"'{name}'"))}; name columns by their property name, "
                + "for example nameof(Record.Property).",
                nameof(options));

        var map = new int[all.Count];
        var columns = new List<WorksheetColumn>(all.Count);
        for (var i = 0; i < all.Count; i++) {
            var name = all[i].PropertyName;
            var written = (included is null || included.Contains(name)) && (excluded is null || !excluded.Contains(name));
            map[i] = written ? columns.Count : -1;
            if (written) columns.Add(ToWorksheetColumn(all[i]));
        }

        return ([.. columns], map);
    }

    private static WorksheetColumn ToWorksheetColumn<T>(SpreadsheetColumn<T> column) where T : class {
        return new WorksheetColumn(column.Header, column.Kind, column.Format, column.Width, column.Choices);
    }

    private async ValueTask WriteWorksheetAsync<TRow>(
        string name,
        IReadOnlyList<WorksheetColumn> columns,
        int[]? map,
        IAsyncEnumerable<TRow> rows,
        Action<SpreadsheetCellWriter, TRow, int> writeRow,
        IReadOnlyList<IReadOnlyList<object?>> footerRows,
        CancellationToken cancellationToken) {
        if (SpreadsheetCells.ValidateWorksheetName(name) is { } reason) throw new SpreadsheetWriteException(reason, name);
        if (_worksheetNames.Contains(name))
            throw new SpreadsheetWriteException(
                $"The workbook already has a worksheet named '{name}'; worksheet names must differ ignoring case.", name);

        if (columns.Count > SpreadsheetCells.MaxColumns)
            throw new SpreadsheetWriteException(
                $"The worksheet '{name}' has {columns.Count} columns; a worksheet holds at most {SpreadsheetCells.MaxColumns}.", name);

        var styles = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++) styles[i] = _styles.StyleFor(columns[i].Kind, columns[i].EffectiveFormat);

        using var xml = new Utf8XmlBuffer();
        var cells = new SpreadsheetCellWriter(name, columns, styles, map, xml);
        var enumerator = rows.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false)) {
            // Read ahead and measure the sample before anything is written: <cols> precedes <sheetData> (FR-019).
            var sample = new List<TRow>(Math.Min(_options.WidthSampleRows, 1024));
            while (sample.Count < _options.WidthSampleRows && await enumerator.MoveNextAsync().ConfigureAwait(false)) {
                var row = enumerator.Current;
                cells.BeginRow(sample.Count + 2, measuring: true);
                writeRow(cells, row, sample.Count + 2);
                cells.EndRow();
                sample.Add(row);
            }

            var archive = await GetArchiveAsync(cancellationToken).ConfigureAwait(false);
            _worksheetNames.Add(name);
            _worksheets.Add(name);
            var entry = archive.CreateEntry($"xl/worksheets/sheet{_worksheets.Count}.xml", CompressionLevel.Optimal);
            entry.LastWriteTime = EntryTimestamp;
            var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false)) {
                WriteWorksheetStart(xml, columns, styles, cells.MeasuredWidths, isFirst: _worksheets.Count == 1);
                cells.WriteHeaderRow();

                var rowNumber = 1;
                foreach (var row in sample) {
                    rowNumber++;
                    cells.BeginRow(rowNumber, measuring: false);
                    writeRow(cells, row, rowNumber);
                    cells.EndRow();
                    if (xml.Length >= Utf8XmlBuffer.FlushThreshold)
                        await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                sample.Clear();
                while (await enumerator.MoveNextAsync().ConfigureAwait(false)) {
                    rowNumber++;
                    cells.BeginRow(rowNumber, measuring: false);
                    writeRow(cells, enumerator.Current, rowNumber);
                    cells.EndRow();
                    if (xml.Length >= Utf8XmlBuffer.FlushThreshold)
                        await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                // Every written row and the first DropdownRows data rows offer the lists, so a template (no records)
                // still has them where a person fills it in (FR-021, FR-022); footer rows end the data, so the lists
                // stop above them (FR-075).
                var dropdownEnd = footerRows.Count > 0
                    ? rowNumber
                    : Math.Min(SpreadsheetCells.MaxRows, Math.Max(rowNumber, _options.DropdownRows + 1));

                foreach (var footer in footerRows) {
                    rowNumber++;
                    cells.BeginRow(rowNumber, measuring: false, mapped: false);
                    cells.WriteValues(footer);
                    cells.EndRow();
                }

                xml.Append("</sheetData>"u8);
                _dropdowns.AppendDataValidations(xml, columns, dropdownEnd, _worksheetNames);
                xml.Append("</worksheet>"u8);
                await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void WriteWorksheetStart(
        Utf8XmlBuffer xml,
        IReadOnlyList<WorksheetColumn> columns,
        int[] styles,
        ReadOnlySpan<int> measuredWidths,
        bool isFirst) {
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">"""u8);
        // The header row stays in place while scrolling (FR-020).
        xml.Append(isFirst
            ? """<sheetViews><sheetView tabSelected="1" workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>"""u8
            : """<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>"""u8);
        xml.Append("""<sheetFormatPr defaultRowHeight="15"/>"""u8);
        if (columns.Count > 0) {
            xml.Append("<cols>"u8);
            for (var i = 0; i < columns.Count; i++) {
                // Measured: the longest of the header and the sampled values, plus room for the cell padding (FR-019).
                var width = columns[i].Width ?? Math.Min(measuredWidths[i] + 2, _options.MaxColumnWidth);
                xml.Append("<col min=\""u8);
                xml.AppendFormatted(i + 1);
                xml.Append("\" max=\""u8);
                xml.AppendFormatted(i + 1);
                xml.Append("\" width=\""u8);
                xml.AppendFormatted(width, "R");
                xml.Append((byte)'"');
                // The column style formats the empty rows below the data too, so text typed there stays text (FR-063).
                if (styles[i] != SpreadsheetStyles.Default) {
                    xml.Append(" style=\""u8);
                    xml.AppendFormatted(styles[i]);
                    xml.Append((byte)'"');
                }

                xml.Append(" customWidth=\"1\"/>"u8);
            }

            xml.Append("</cols>"u8);
        }

        xml.Append("<sheetData>"u8);
    }

    private async ValueTask<ZipArchive> GetArchiveAsync(CancellationToken cancellationToken) {
        return _archive ??= await ZipArchive.CreateAsync(
                _destination, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask WritePackagePartsAsync(ZipArchive archive, CancellationToken cancellationToken) {
        using var xml = new Utf8XmlBuffer(16 * 1024);
        var listsWorksheet = _dropdowns.ListsWorksheetName;
        var sheetCount = _worksheets.Count + (listsWorksheet is null ? 0 : 1);
        if (listsWorksheet is not null) {
            _dropdowns.AppendListsWorksheet(xml);
            await WriteEntryAsync(archive, $"xl/worksheets/sheet{sheetCount}.xml", xml, cancellationToken).ConfigureAwait(false);
        }

        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>"""u8);
        for (var i = 0; i < _worksheets.Count; i++) {
            xml.Append("<sheet name=\""u8);
            xml.AppendAttribute(_worksheets[i]);
            xml.Append("\" sheetId=\""u8);
            xml.AppendFormatted(i + 1);
            xml.Append("\" r:id=\"rId"u8);
            xml.AppendFormatted(i + 1);
            xml.Append("\"/>"u8);
        }

        if (listsWorksheet is not null) {
            // Hidden, so the lists behind the dropdowns are not mistaken for data, and a reader skips the sheet.
            xml.Append("<sheet name=\""u8);
            xml.AppendAttribute(listsWorksheet);
            xml.Append("\" sheetId=\""u8);
            xml.AppendFormatted(sheetCount);
            xml.Append("\" state=\"hidden\" r:id=\"rId"u8);
            xml.AppendFormatted(sheetCount);
            xml.Append("\"/>"u8);
        }

        xml.Append("</sheets></workbook>"u8);
        await WriteEntryAsync(archive, "xl/workbook.xml", xml, cancellationToken).ConfigureAwait(false);

        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""u8);
        for (var i = 0; i < sheetCount; i++) {
            xml.Append("<Relationship Id=\"rId"u8);
            xml.AppendFormatted(i + 1);
            xml.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet"u8);
            xml.AppendFormatted(i + 1);
            xml.Append(".xml\"/>"u8);
        }

        xml.Append("<Relationship Id=\"rId"u8);
        xml.AppendFormatted(sheetCount + 1);
        xml.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"u8);
        xml.Append("</Relationships>"u8);
        await WriteEntryAsync(archive, "xl/_rels/workbook.xml.rels", xml, cancellationToken).ConfigureAwait(false);

        _styles.Write(xml);
        await WriteEntryAsync(archive, "xl/styles.xml", xml, cancellationToken).ConfigureAwait(false);

        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">"""u8);
        xml.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>"""u8);
        xml.Append("""<Default Extension="xml" ContentType="application/xml"/>"""u8);
        xml.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>"""u8);
        for (var i = 0; i < sheetCount; i++) {
            xml.Append("<Override PartName=\"/xl/worksheets/sheet"u8);
            xml.AppendFormatted(i + 1);
            xml.Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"u8);
        }

        xml.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>"""u8);
        xml.Append("</Types>"u8);
        await WriteEntryAsync(archive, "[Content_Types].xml", xml, cancellationToken).ConfigureAwait(false);

        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""u8);
        await WriteEntryAsync(archive, "_rels/.rels", xml, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteEntryAsync(
        ZipArchive archive, string name, Utf8XmlBuffer xml, CancellationToken cancellationToken) {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTimestamp;
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false)) {
            await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Presents a synchronous sequence to the one asynchronous worksheet path.</summary>
    private sealed class SynchronousRecords<T>(IEnumerable<T> records) : IAsyncEnumerable<T> {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return new Enumerator(records.GetEnumerator(), cancellationToken);
        }

        private sealed class Enumerator(IEnumerator<T> inner, CancellationToken cancellationToken) : IAsyncEnumerator<T> {
            public T Current => inner.Current;

            public ValueTask<bool> MoveNextAsync() {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(inner.MoveNext());
            }

            public ValueTask DisposeAsync() {
                inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
