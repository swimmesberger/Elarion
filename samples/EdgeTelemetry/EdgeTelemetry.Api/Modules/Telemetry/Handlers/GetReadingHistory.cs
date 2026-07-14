using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>Recent history for a device+metric, newest first — self-mapping, no mapper argument.</summary>
[Handler("telemetry.history")]
public sealed class GetReadingHistory(NpgsqlDataSource db)
    : IHandler<GetReadingHistory.Query, Result<List<ReadingRow>>> {
    public sealed record Query(string DeviceId, string Metric, int Limit) : IQuery;

    public async ValueTask<Result<List<ReadingRow>>> HandleAsync(Query query, CancellationToken ct) =>
        await db.QueryAsync<ReadingRow>(
            $"""
             {ReadingRow.Select}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
             ORDER BY recorded_at DESC LIMIT {query.Limit}
             """,
            ct);
}
