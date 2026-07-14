using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>Recent history for a device+metric, newest first.</summary>
[Handler("telemetry.history")]
public sealed class GetReadingHistory(NpgsqlDataSource db, ISqlRowMapper<ReadingRow> mapper)
    : IHandler<GetReadingHistory.Query, Result<List<ReadingRow>>> {
    public sealed record Query(string DeviceId, string Metric, int Limit) : IQuery;

    public async ValueTask<Result<List<ReadingRow>>> HandleAsync(Query query, CancellationToken ct) {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QueryAsync(
            mapper,
            $"""
             SELECT {ReadingRowSqlMapper.Columns.All:raw} FROM {ReadingRowSqlMapper.TableName:raw}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
             ORDER BY recorded_at DESC LIMIT {query.Limit}
             """,
            ct);
    }
}
