using Elarion.Abstractions;
using Elarion.Sql;
using Npgsql;

namespace EdgeTelemetry.Api.Modules.Telemetry.Handlers;

/// <summary>
/// Batch ingest: one transaction per batch, one generated <c>BindParameters</c> call per row. Npgsql
/// auto-prepares the repeated INSERT, so a steady ingest loop runs on a prepared statement without
/// any setup code. <c>ON CONFLICT DO NOTHING</c> makes retransmits idempotent by constraint — device
/// gateways resend batches they are not sure were received, and a replayed row hits the hypertable's
/// composite key; the response reports how many rows were actually new.
/// </summary>
[Handler("telemetry.ingest")]
public sealed class IngestReadings(NpgsqlDataSource db, ISqlRowMapper<ReadingRow> mapper, TimeProvider time)
    : IHandler<IngestReadings.Command, Result<IngestResult>> {
    public sealed record Command(ReadingInput[] Readings) : ICommand;

    public async ValueTask<Result<IngestResult>> HandleAsync(Command command, CancellationToken ct) {
        if (command.Readings.Length == 0) {
            return AppError.Validation("The batch is empty; send at least one reading.");
        }

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        // The generated full-row INSERT constant; the conflict clause composes at compile time.
        insert.CommandText = ReadingRowSqlMapper.Insert + " ON CONFLICT DO NOTHING";
        var written = 0;
        foreach (var input in command.Readings) {
            var row = new ReadingRow {
                DeviceId = input.DeviceId,
                Metric = input.Metric,
                // PostgreSQL timestamptz stores an instant; normalize whatever offset the device sent.
                RecordedAt = (input.RecordedAt ?? time.GetUtcNow()).ToUniversalTime(),
                Value = input.Value,
                Meta = input.Meta,
            };
            insert.Parameters.Clear();
            mapper.BindParameters(insert, row);
            written += await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new IngestResult(written);
    }
}
