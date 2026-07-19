using Elarion.Abstractions;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Batch ingest: <c>InsertManyAsync</c> reuses one prepared command per row, so the handler just projects
/// inputs to rows. Because this command handler injects <see cref="ISqlSession"/> and the host registered
/// <c>AddElarionSqlUnitOfWork()</c>, the framework transaction decorator wraps the whole batch in one
/// transaction on the session's connection. <c>ON CONFLICT DO NOTHING</c> makes retransmits idempotent by
/// constraint — device gateways resend batches they are not sure were received, and a replayed row hits
/// the hypertable's composite key; the response reports how many rows were actually new.
/// </summary>
[Handler("telemetry.ingest")]
public sealed class IngestReadings(ISqlSession db, TimeProvider time)
    : IHandler<IngestReadings.Command, Result<IngestResult>> {
    public sealed record Command(ReadingInput[] Readings) : ICommand;

    public async ValueTask<Result<IngestResult>> HandleAsync(Command command, CancellationToken ct) {
        if (command.Readings.Length == 0) return AppError.Validation("The batch is empty; send at least one reading.");

        var rows = command.Readings.Select(input => new ReadingRow {
            DeviceId = input.DeviceId,
            Metric = input.Metric,
            // PostgreSQL timestamptz stores an instant; normalize whatever offset the device sent.
            RecordedAt = (input.RecordedAt ?? time.GetUtcNow()).ToUniversalTime(),
            Value = input.Value,
            Meta = input.Meta
        });

        var written = await db.InsertManyAsync(rows, " ON CONFLICT DO NOTHING", ct);
        return new IngestResult(written);
    }
}
