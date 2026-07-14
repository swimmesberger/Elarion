using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Hourly aggregate — the "full power of SQL" leg: TimescaleDB's <c>time_bucket</c> + plain aggregates
/// stay SQL, and the self-mapping <c>MetricBucket</c> row binds the projection by column name (a
/// <c>[SqlRecord]</c> needs no physical table; the SELECT is hand-written, not <c>MetricBucket.Select</c>).
/// Extension functions need no framework support. <c>ReadingRow.Table</c> splices the source table.
/// </summary>
[Handler("telemetry.stats")]
public sealed class GetMetricStats(NpgsqlDataSource db, TimeProvider time)
    : IHandler<GetMetricStats.Query, Result<List<MetricBucket>>> {
    public sealed record Query(string DeviceId, string Metric, int Hours) : IQuery;

    public async ValueTask<Result<List<MetricBucket>>> HandleAsync(Query query, CancellationToken ct) {
        var since = time.GetUtcNow().AddHours(-query.Hours);
        return await db.QueryAsync<MetricBucket>(
            $"""
             SELECT time_bucket(INTERVAL '1 hour', recorded_at) AS bucket,
                    count(*)   AS samples,
                    avg(value) AS avg_value,
                    min(value) AS min_value,
                    max(value) AS max_value
             FROM {ReadingRow.Table}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric} AND recorded_at >= {since}
             GROUP BY 1
             ORDER BY 1
             """,
            ct);
    }
}
