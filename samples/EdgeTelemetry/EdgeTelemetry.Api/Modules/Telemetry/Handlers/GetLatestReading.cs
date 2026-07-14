using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Latest sample for a device+metric — interpolated values bind as parameters, the <c>:raw</c> holes
/// splice the generated constants, and the mapper materializes the row. Full SQL, no translation
/// layer; a miss is a <c>Result</c> failure that the HTTP layer renders as 404.
/// </summary>
[Handler("telemetry.latest")]
public sealed class GetLatestReading(NpgsqlDataSource db, ISqlRowMapper<ReadingRow> mapper)
    : IHandler<GetLatestReading.Query, Result<ReadingRow>> {
    public sealed record Query(string DeviceId, string Metric) : IQuery;

    public async ValueTask<Result<ReadingRow>> HandleAsync(Query query, CancellationToken ct) {
        await using var connection = await db.OpenConnectionAsync(ct);
        var reading = await connection.QueryFirstOrDefaultAsync(
            mapper,
            $"""
             SELECT {ReadingRowSqlMapper.Columns.All:raw} FROM {ReadingRowSqlMapper.TableName:raw}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
             ORDER BY recorded_at DESC LIMIT 1
             """,
            ct);

        return reading is null
            ? AppError.NotFound($"No '{query.Metric}' reading for device '{query.DeviceId}'.")
            : reading;
    }
}
