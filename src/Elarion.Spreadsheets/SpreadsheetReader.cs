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
public sealed partial class SpreadsheetReader {
    private readonly SpreadsheetReadOptions _defaults;

    /// <summary>Creates a reader with the application's options; every argument is optional.</summary>
    /// <param name="options">The application's options; <see langword="null"/> uses the defaults.</param>
    public SpreadsheetReader(IOptions<SpreadsheetOptions>? options = null) {
        _defaults = options?.Value.ReadDefaults ?? new SpreadsheetReadOptions();
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

        var inspection = WorkbookInspector.Inspect(content, options.MaxUnpackedBytes, out var error);
        if (inspection is null) return new SpreadsheetReadDocument(this, options, null, error);

        try {
            return new SpreadsheetReadDocument(this, options, SylvanWorkbookSource.Open(content, inspection), null);
        }
        catch (UnreadableWorkbookException) {
            return new SpreadsheetReadDocument(this, options, null, SpreadsheetErrors.Unreadable("it is damaged or incomplete"));
        }
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
