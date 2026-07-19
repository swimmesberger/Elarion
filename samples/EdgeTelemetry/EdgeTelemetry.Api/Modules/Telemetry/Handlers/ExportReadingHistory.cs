using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// A finite, cold export of a device metric's history. Unlike the buffered history query, rows stay on the
/// database reader until the SSE response consumes them — the session's connection stays open for the whole
/// enumeration and is released when the request scope ends.
/// </summary>
[AllowAnonymous]
public sealed class ExportReadingHistory(ISqlSession db)
    : IStreamHandler<ExportReadingHistory.Query, ReadingRow> {
    public sealed record Query(string DeviceId, string Metric, int Limit) : IQuery;

    public ValueTask<Result<IAsyncEnumerable<ReadingRow>>> HandleAsync(Query query, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(query.DeviceId))
            return ValueTask.FromResult<Result<IAsyncEnumerable<ReadingRow>>>(
                AppError.Validation("A device id is required."));

        if (string.IsNullOrWhiteSpace(query.Metric))
            return ValueTask.FromResult<Result<IAsyncEnumerable<ReadingRow>>>(
                AppError.Validation("A metric is required."));

        if (query.Limit is < 1 or > 1_000)
            return ValueTask.FromResult<Result<IAsyncEnumerable<ReadingRow>>>(
                AppError.Validation("Limit must be between 1 and 1000."));

        return ValueTask.FromResult(Result<IAsyncEnumerable<ReadingRow>>.Success(
            db.QueryUnbufferedAsync<ReadingRow>(
                $"""
                 {ReadingRow.Select}
                 WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
                 ORDER BY recorded_at DESC LIMIT {query.Limit}
                 """,
                ct)));
    }
}
