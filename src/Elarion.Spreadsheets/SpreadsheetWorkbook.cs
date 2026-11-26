using System.IO.Compression;
using Elarion.Spreadsheets.Generated;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets;

/// <summary>
/// Writes spreadsheet records as an <c>.xlsx</c> workbook, forward-only to a stream (ADR-0108): one worksheet per
/// <see cref="AddWorksheetAsync{T}(IEnumerable{T}, SpreadsheetWorksheetOptions?, CancellationToken)"/> call, in the
/// order written, each with a bold, filled, bordered and frozen header row, then one row per record.
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

        options ??= SpreadsheetWriteOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.WidthSampleRows, nameof(options));
        if (!(options.MaxColumnWidth >= 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxColumnWidth, "MaxColumnWidth must be one or more.");

        return new SpreadsheetWorkbook(destination, options);
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

    private async ValueTask AddWorksheetCoreAsync<T>(
        IAsyncEnumerable<T> records,
        SpreadsheetWorksheetOptions? options,
        CancellationToken cancellationToken) where T : class, ISpreadsheetRecord<T> {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("An earlier write of this workbook failed; it is incomplete and takes no further worksheets.");

        try {
            await WriteWorksheetAsync(T.SpreadsheetMapper, records, options?.Name, cancellationToken).ConfigureAwait(false);
        }
        catch {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask WriteWorksheetAsync<T>(
        ISpreadsheetRowMapper<T> mapper,
        IAsyncEnumerable<T> records,
        string? requestedName,
        CancellationToken cancellationToken) where T : class {
        var name = requestedName ?? mapper.WorksheetName;
        if (SpreadsheetCells.ValidateWorksheetName(name) is { } reason) throw new SpreadsheetWriteException(reason, name);
        if (_worksheetNames.Contains(name))
            throw new SpreadsheetWriteException(
                $"The workbook already has a worksheet named '{name}'; worksheet names must differ ignoring case.", name);

        var columns = mapper.Columns;
        if (columns.Count > SpreadsheetCells.MaxColumns)
            throw new SpreadsheetWriteException(
                $"The worksheet '{name}' has {columns.Count} columns; a worksheet holds at most {SpreadsheetCells.MaxColumns}.", name);

        var headers = new string[columns.Count];
        var kinds = new SpreadsheetValueKind[columns.Count];
        var formats = new string?[columns.Count];
        var styles = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++) {
            var column = columns[i];
            headers[i] = column.Header;
            kinds[i] = column.Kind;
            formats[i] = column.Kind is SpreadsheetValueKind.Integer or SpreadsheetValueKind.Number
                or SpreadsheetValueKind.Date or SpreadsheetValueKind.DateTime or SpreadsheetValueKind.Time
                ? column.Format
                : null;
            styles[i] = _styles.StyleFor(column.Kind, formats[i]);
        }

        using var xml = new Utf8XmlBuffer();
        var cells = new SpreadsheetCellWriter(name, headers, kinds, formats, styles, xml);
        var enumerator = records.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false)) {
            // Read ahead and measure the sample before anything is written: <cols> precedes <sheetData> (FR-019).
            var sample = new List<T>(Math.Min(_options.WidthSampleRows, 1024));
            while (sample.Count < _options.WidthSampleRows && await enumerator.MoveNextAsync().ConfigureAwait(false)) {
                var record = enumerator.Current;
                cells.BeginRow(sample.Count + 2, measuring: true);
                WriteRecord(mapper, cells, record, name, sample.Count + 2);
                cells.EndRow();
                sample.Add(record);
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
                foreach (var record in sample) {
                    rowNumber++;
                    cells.BeginRow(rowNumber, measuring: false);
                    WriteRecord(mapper, cells, record, name, rowNumber);
                    cells.EndRow();
                    if (xml.Length >= Utf8XmlBuffer.FlushThreshold)
                        await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                sample.Clear();
                while (await enumerator.MoveNextAsync().ConfigureAwait(false)) {
                    rowNumber++;
                    cells.BeginRow(rowNumber, measuring: false);
                    WriteRecord(mapper, cells, enumerator.Current, name, rowNumber);
                    cells.EndRow();
                    if (xml.Length >= Utf8XmlBuffer.FlushThreshold)
                        await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                xml.Append("</sheetData>"u8);
                xml.Append("</worksheet>"u8);
                await xml.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void WriteRecord<T>(
        ISpreadsheetRowMapper<T> mapper, SpreadsheetCellWriter cells, T? record, string worksheet, int rowNumber)
        where T : class {
        if (record is null)
            throw new SpreadsheetWriteException(
                $"Cannot write row {rowNumber} of worksheet '{worksheet}': the record is null.", worksheet, rowNumber);

        mapper.WriteRow(record, cells);
    }

    private void WriteWorksheetStart<T>(
        Utf8XmlBuffer xml,
        IReadOnlyList<SpreadsheetColumn<T>> columns,
        int[] styles,
        ReadOnlySpan<int> measuredWidths,
        bool isFirst) where T : class {
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

        xml.Append("</sheets></workbook>"u8);
        await WriteEntryAsync(archive, "xl/workbook.xml", xml, cancellationToken).ConfigureAwait(false);

        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""u8);
        for (var i = 0; i < _worksheets.Count; i++) {
            xml.Append("<Relationship Id=\"rId"u8);
            xml.AppendFormatted(i + 1);
            xml.Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet"u8);
            xml.AppendFormatted(i + 1);
            xml.Append(".xml\"/>"u8);
        }

        xml.Append("<Relationship Id=\"rId"u8);
        xml.AppendFormatted(_worksheets.Count + 1);
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
        for (var i = 0; i < _worksheets.Count; i++) {
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
