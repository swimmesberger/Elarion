using Elarion.Abstractions;
using Elarion.Spreadsheets;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Imports readings from an uploaded workbook (<c>.xlsx</c>, <c>.xlsb</c> or <c>.xls</c>). The reader returns every
/// row with its located problems, including the validation attributes of <see cref="ReadingSheetRow"/>; one broken
/// row rejects the whole file with a validation error per problem, so the uploader fixes the file and sends it again.
/// </summary>
/// <remarks>
/// The upload is an <see cref="ElarionFile"/>, which binds from the JSON body's base64 envelope. Like
/// <c>telemetry.ingest</c>, the insert runs in the unit of work's transaction and skips readings already stored.
/// </remarks>
[HttpEndpoint("readings/import")]
public sealed class ImportReadingsSpreadsheet(SpreadsheetReader reader, ISqlSession db)
    : IHandler<ImportReadingsSpreadsheet.Command, Result<IngestResult>> {
    public sealed record Command : ICommand {
        public required ElarionFile Workbook { get; init; }
    }

    public async ValueTask<Result<IngestResult>> HandleAsync(Command command, CancellationToken ct) {
        using var content = new MemoryStream(command.Workbook.Bytes.ToArray(), writable: false);
        var result = await reader.ReadAsync<ReadingSheetRow>(content, cancellationToken: ct);
        if (result.HasErrors) return result.ToAppError("workbook");

        var rows = result.Records.Select(record => new ReadingRow {
            DeviceId = record.DeviceId,
            Metric = record.Metric,
            RecordedAt = new DateTimeOffset(DateTime.SpecifyKind(record.RecordedAt, DateTimeKind.Utc)),
            Value = record.Value
        });

        var written = await db.InsertManyAsync(rows, " ON CONFLICT DO NOTHING", ct);
        return new IngestResult(written);
    }
}
