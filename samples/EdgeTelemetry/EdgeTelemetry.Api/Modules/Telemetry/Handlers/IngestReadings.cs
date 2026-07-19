using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Batch ingest: <c>InsertManyAsync</c> owns the one-transaction, reused-prepared-command loop, so the
/// handler just projects inputs to rows. <c>ON CONFLICT DO NOTHING</c> makes retransmits idempotent by
/// constraint — device gateways resend batches they are not sure were received, and a replayed row hits
/// the hypertable's composite key; the response reports how many rows were actually new.
/// </summary>
[Handler("telemetry.ingest")]
public sealed class IngestReadings(NpgsqlDataSource db, TimeProvider time)
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
