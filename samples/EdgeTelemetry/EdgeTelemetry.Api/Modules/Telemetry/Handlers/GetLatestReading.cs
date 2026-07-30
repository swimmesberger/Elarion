using Elarion.Abstractions;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Latest sample for a device+metric — self-mapping resolves the row's mapper, interpolated values
/// bind as parameters, and <c>ReadingRow.Select</c> splices the generated SELECT-list (no <c>:raw</c>).
/// Full SQL, no translation layer; a miss is a <c>Result</c> failure the HTTP layer renders as 404.
/// </summary>
/// <remarks>
/// The <c>[HttpEndpoint]</c> proves the generated REST tier under NativeAOT: the generator classifies
/// <c>DeviceId</c> as the <c>{deviceId}</c> route token and <c>Metric</c> as a required query parameter at
/// compile time and emits an AOT-safe <c>RequestDelegate</c> registration (ADR-0071) — no reflection-based
/// binding anywhere in the published binary.
/// </remarks>
[Handler("telemetry.latest")]
[HttpEndpoint("devices/{deviceId}/latest")]
public sealed class GetLatestReading(ISqlSession db)
    : IHandler<GetLatestReading.Query, Result<ReadingRow>> {
    public sealed record Query(string DeviceId, string Metric) : IQuery;

    public async ValueTask<Result<ReadingRow>> HandleAsync(Query query, CancellationToken ct) {
        var reading = await db.QueryFirstOrDefaultAsync<ReadingRow>(
            $"""
             {ReadingRow.Select}
             WHERE device_id = {query.DeviceId} AND metric = {query.Metric}
             ORDER BY recorded_at DESC LIMIT 1
             """,
            ct);

        return reading is null
            ? AppError.NotFound($"No '{query.Metric}' reading for device '{query.DeviceId}'.")
            : reading;
    }
}
