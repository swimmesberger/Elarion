using System.Runtime.InteropServices;
using System.Text.Json;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Validation;
using Elarion.Spreadsheets.Reading;
using Microsoft.Extensions.Options;

namespace Elarion.Spreadsheets;

/// <summary>
/// Reads uploaded workbooks (<c>.xlsx</c>, <c>.xlsb</c>, <c>.xls</c>, recognized from the content) into spreadsheet
/// records or plain rows (ADR-0108). Registered by <c>AddElarionSpreadsheets</c>; inject it into a handler.
/// </summary>
/// <remarks>
/// <para>
/// A file a user uploaded is input, not infrastructure: whatever the file holds, a read returns a result with every row
/// and every located problem, and throws only on cancellation and on misconfiguration (FR-049).
/// </para>
/// <para>
/// The reader buffers the whole stream, refuses a workbook that unpacks to more than
/// <see cref="SpreadsheetReadOptions.MaxUnpackedBytes"/> before unpacking it, and never processes a document type
/// definition (FR-055, FR-057).
/// </para>
/// </remarks>
public sealed class SpreadsheetReader {
    private readonly SpreadsheetReadOptions _defaults;

    /// <summary>Creates a reader; every argument is optional, and dependency injection supplies the registered ones.</summary>
    /// <param name="options">The application's options; <see langword="null"/> uses the defaults.</param>
    /// <param name="validator">
    /// The request validator that checks each record read (FR-047); required for a record that carries validation
    /// attributes.
    /// </param>
    /// <param name="jsonSerialization">
    /// The canonical JSON settings, whose naming policy turns the validator's wire-named paths back into columns.
    /// </param>
    public SpreadsheetReader(
        IOptions<SpreadsheetOptions>? options = null,
        IRequestValidator? validator = null,
        IElarionJsonSerialization? jsonSerialization = null) {
        _defaults = options?.Value.ReadDefaults ?? new SpreadsheetReadOptions();
        Validator = validator;
        NamingPolicy = jsonSerialization?.Options.PropertyNamingPolicy;
    }

    internal IRequestValidator? Validator { get; }

    internal JsonNamingPolicy? NamingPolicy { get; }

    /// <summary>
    /// Opens <paramref name="workbook"/>, reads one worksheet as records of <typeparamref name="T"/>, and releases the
    /// workbook: the shortcut for a file with one worksheet of interest.
    /// </summary>
    /// <param name="workbook">The uploaded file; read to its end, and left open.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="SpreadsheetOptions.ReadDefaults"/>.</param>
    /// <param name="selection">The worksheet and its header row; <see langword="null"/> takes the first visible worksheet.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> carries validation attributes, but no request validator is registered or it holds no
    /// metadata for the record (FR-048).
    /// </exception>
    /// <example>
    /// <code>
    /// var result = await reader.ReadAsync&lt;ContactRow&gt;(request.Workbook.OpenReadStream(), cancellationToken: ct);
    /// if (result.HasErrors) return result.ToAppError("workbook");
    /// </code>
    /// </example>
    public async ValueTask<SpreadsheetReadResult<T>> ReadAsync<T>(
        Stream workbook,
        SpreadsheetReadOptions? options = null,
        SpreadsheetWorksheetSelection? selection = null,
        CancellationToken cancellationToken = default)
        where T : class, ISpreadsheetRecord<T> {
        RecordReader.EnsureValidationConfigured<T>(Validator);
        using var document = await OpenAsync(workbook, options, cancellationToken).ConfigureAwait(false);
        return await document.ReadAsync<T>(selection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="workbook"/> once, so that its worksheets can be read as several record types (FR-069).
    /// </summary>
    /// <param name="workbook">The uploaded file; read to its end, and left open.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="SpreadsheetOptions.ReadDefaults"/>.</param>
    /// <param name="cancellationToken">Cancels buffering the stream.</param>
    /// <returns>The document; a file that cannot be read opens with its one error in <see cref="SpreadsheetReadDocument.FileErrors"/>.</returns>
    /// <example>
    /// <code>
    /// using var document = await reader.OpenAsync(request.Workbook.OpenReadStream(), cancellationToken: ct);
    /// var contacts = await document.ReadAsync&lt;ContactRow&gt;(new() { Name = "Contacts" }, ct);
    /// var accounts = await document.ReadAsync&lt;BankAccountRow&gt;(new() { Name = "Bank accounts" }, ct);
    /// </code>
    /// </example>
    public async ValueTask<SpreadsheetReadDocument> OpenAsync(
        Stream workbook, SpreadsheetReadOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(workbook);
        options ??= _defaults;
        options.Validate();

        var content = new MemoryStream();
        if (!await CopyBoundedAsync(workbook, content, options.MaxUnpackedBytes, cancellationToken).ConfigureAwait(false))
            return new SpreadsheetReadDocument(this, options, null, SpreadsheetErrors.TooLarge(options.MaxUnpackedBytes));

        return OpenBuffered(content, options);
    }

    /// <summary>
    /// Opens a workbook the caller already holds in memory, such as an <c>ElarionFile</c>'s <c>Bytes</c>, without copying or
    /// awaiting anything (FR-079); otherwise the same as <see cref="OpenAsync"/>.
    /// </summary>
    /// <param name="workbook">The workbook's bytes; the document reads them in place, so do not change them until it is disposed.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="SpreadsheetOptions.ReadDefaults"/>.</param>
    /// <returns>The document; a file that cannot be read opens with its one error in <see cref="SpreadsheetReadDocument.FileErrors"/>.</returns>
    public SpreadsheetReadDocument Open(ReadOnlyMemory<byte> workbook, SpreadsheetReadOptions? options = null) {
        options ??= _defaults;
        options.Validate();

        if (workbook.Length > options.MaxUnpackedBytes)
            return new SpreadsheetReadDocument(this, options, null, SpreadsheetErrors.TooLarge(options.MaxUnpackedBytes));

        // The stream wraps the caller's array in place; memory not backed by an array is copied once.
        var content = MemoryMarshal.TryGetArray(workbook, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false, publiclyVisible: true)
            : new MemoryStream(workbook.ToArray(), 0, workbook.Length, writable: false, publiclyVisible: true);
        return OpenBuffered(content, options);
    }

    /// <summary>
    /// Reads one worksheet of a workbook the caller already holds in memory as plain rows, synchronously (FR-079);
    /// otherwise the same as <see cref="ReadRowsAsync"/>.
    /// </summary>
    /// <param name="workbook">The workbook's bytes.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="SpreadsheetOptions.ReadDefaults"/>.</param>
    /// <param name="selection">The worksheet; <see langword="null"/> takes the first visible one.</param>
    /// <example>
    /// <code>
    /// var sheet = reader.ReadRows(file.Bytes);
    /// </code>
    /// </example>
    public SpreadsheetRowsResult ReadRows(
        ReadOnlyMemory<byte> workbook,
        SpreadsheetReadOptions? options = null,
        SpreadsheetWorksheetSelection? selection = null) {
        using var document = Open(workbook, options);
        return document.ReadRows(selection);
    }

    private SpreadsheetReadDocument OpenBuffered(MemoryStream content, SpreadsheetReadOptions options) {
        var inspection = WorkbookInspector.Inspect(content, options.MaxUnpackedBytes, out var error);
        if (inspection is null) return new SpreadsheetReadDocument(this, options, null, error);

        try {
            return new SpreadsheetReadDocument(this, options, SylvanWorkbookSource.Open(content, inspection), null);
        }
        catch (UnreadableWorkbookException) {
            return new SpreadsheetReadDocument(this, options, null, SpreadsheetErrors.Unreadable("it is damaged or incomplete"));
        }
    }

    /// <summary>
    /// Opens <paramref name="workbook"/>, reads one worksheet without a record as rows of typed cells, and releases the
    /// workbook (FR-058): for a foreign layout, such as a bank's statement export, whose header row the application finds.
    /// </summary>
    /// <param name="workbook">The uploaded file; read to its end, and left open.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="SpreadsheetOptions.ReadDefaults"/>.</param>
    /// <param name="selection">The worksheet; <see langword="null"/> takes the first visible one.</param>
    /// <param name="cancellationToken">Cancels buffering the stream.</param>
    public async ValueTask<SpreadsheetRowsResult> ReadRowsAsync(
        Stream workbook,
        SpreadsheetReadOptions? options = null,
        SpreadsheetWorksheetSelection? selection = null,
        CancellationToken cancellationToken = default) {
        using var document = await OpenAsync(workbook, options, cancellationToken).ConfigureAwait(false);
        return document.ReadRows(selection);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes; <see langword="false"/> when the source holds more.</summary>
    private static async ValueTask<bool> CopyBoundedAsync(
        Stream source, MemoryStream destination, long limit, CancellationToken cancellationToken) {
        if (source.CanSeek) {
            var remaining = source.Length - source.Position;
            if (remaining > limit) return false;

            destination.Capacity = (int)Math.Min(remaining, Array.MaxLength);
        }

        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
            total += read;
            if (total > limit || total > Array.MaxLength) return false;

            destination.Write(buffer, 0, read);
        }

        return true;
    }
}
