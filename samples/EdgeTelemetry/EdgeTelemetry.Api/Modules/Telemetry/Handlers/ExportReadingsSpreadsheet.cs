using System.Runtime.CompilerServices;
using Elarion.Abstractions;
using Elarion.Spreadsheets;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// A device metric's history as an <c>.xlsx</c> download. The workbook streams to the response body while the rows
/// come off the database reader, so a large export holds only the rows the writer measures for its column widths.
/// </summary>
/// <remarks>
/// <c>[HttpEndpoint]</c> alone: a streamed file reaches only HTTP. The rows are enumerated after the pipeline, inside
/// the request's scope, where the session is still open.
/// </remarks>
[HttpEndpoint("devices/{deviceId}/readings.xlsx")]
public sealed class ExportReadingsSpreadsheet(ISqlSession db)
    : IHandler<ExportReadingsSpreadsheet.Query, Result<ElarionStreamedFile>> {
    public sealed record Query(string DeviceId, string Metric) : IQuery;

    public ValueTask<Result<ElarionStreamedFile>> HandleAsync(Query query, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(query.Metric))
            return ValueTask.FromResult<Result<ElarionStreamedFile>>(AppError.Validation("A metric is required."));

        var rows = db.QueryUnbufferedAsync<ReadingRow>(
            $"""
             {ReadingRow.Select}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
             ORDER BY recorded_at
             """,
            ct);

        return ValueTask.FromResult(Result<ElarionStreamedFile>.Success(
            SpreadsheetWorkbook.ToStreamedFile(ToSheetRows(rows), $"{query.DeviceId}-{query.Metric}.xlsx")));
    }

    private static async IAsyncEnumerable<ReadingSheetRow> ToSheetRows(
        IAsyncEnumerable<ReadingRow> rows, [EnumeratorCancellation] CancellationToken ct = default) {
        await foreach (var row in rows.WithCancellation(ct)) {
            yield return new ReadingSheetRow {
                DeviceId = row.DeviceId,
                Metric = row.Metric,
                RecordedAt = row.RecordedAt.UtcDateTime,
                Value = row.Value
            };
        }
    }
}
