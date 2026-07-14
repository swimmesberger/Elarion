using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Hourly aggregate — the "full power of SQL" leg: TimescaleDB's <c>time_bucket</c> + plain
/// aggregates stay SQL, and the <c>MetricBucket</c> mapper binds the projection by column name (a
/// <c>[SqlRecord]</c> needs no physical table). Extension functions need no framework support.
/// </summary>
[Handler("telemetry.stats")]
public sealed class GetMetricStats(NpgsqlDataSource db, ISqlRowMapper<MetricBucket> mapper, TimeProvider time)
    : IHandler<GetMetricStats.Query, Result<List<MetricBucket>>> {
    public sealed record Query(string DeviceId, string Metric, int Hours) : IQuery;

    public async ValueTask<Result<List<MetricBucket>>> HandleAsync(Query query, CancellationToken ct) {
        var since = time.GetUtcNow().AddHours(-query.Hours);
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.QueryAsync(
            mapper,
            $"""
             SELECT time_bucket(INTERVAL '1 hour', recorded_at) AS bucket,
                    count(*)   AS samples,
                    avg(value) AS avg_value,
                    min(value) AS min_value,
                    max(value) AS max_value
             FROM {ReadingRowSqlMapper.TableName:raw}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric} AND recorded_at >= {since}
             GROUP BY 1
             ORDER BY 1
             """,
            ct);
    }
}
