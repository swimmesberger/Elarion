using EdgeTelemetry.Api;
using Elarion.Abstractions.Serialization;
using Elarion.Migrations.PostgreSql;
using Elarion.Sql;
using Npgsql;

// EdgeTelemetry: the Elarion EF-free NativeAOT tier end-to-end. A telemetry ingest node published as
// a native binary — the use case is cold-start speed and footprint (edge boxes, scale-to-zero,
// sidecars, CLI-style lifetimes), so the whole data story is the AOT-tier pair:
//
//   Elarion.Migrations.PostgreSql (ADR-0057)  embedded V__ scripts, applied before the host is ready
//   Elarion.Sql                   (ADR-0058)  [SqlRecord] generated mappers + safe SQL interpolation
//
// No EF, no reflection, no runtime codegen: every mapper, every JSON contract, and every route
// handler is compiled ahead of time. `dotnet publish` produces a self-contained native executable.

var builder = WebApplication.CreateSlimBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Telemetry")
    ?? throw new InvalidOperationException("Missing connection string 'Telemetry'.");

// One pooled data source for the host. The slim builder maps exactly the PostgreSQL types this app
// uses (uuid, text, timestamptz, double precision, jsonb-as-string) — the full builder works too,
// the slim one just keeps the trimmed binary smaller. `Max Auto Prepare` turns the hot INSERT and
// SELECT statements into prepared statements automatically after a few executions.
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).Build();
builder.Services.AddSingleton(dataSource);

// Canonical JSON (ADR-0023): ONE source-generated context serves both the HTTP contracts and the
// [SqlJson] meta column — the generated ReadingRowSqlMapper takes this accessor via its constructor,
// which AddElarionSqlMappers (generated for this assembly) wires up.
builder.Services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(TelemetryJsonContext.Default));
builder.Services.AddElarionSqlMappers();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TelemetryJsonContext.Default));

// Schema before traffic: the hosted service applies pending embedded migrations and fails startup on
// error — an edge node serving against a half-migrated schema is worse than one that does not start.
builder.Services.AddElarionPostgreSqlMigrations(
    dataSource,
    options => options.AddScripts(typeof(Program).Assembly, "EdgeTelemetry.Api.Migrations."));

builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

// Ingest: one transaction per batch, one generated BindParameters call per row. Npgsql auto-prepares
// the repeated INSERT, so a steady ingest loop runs on a prepared statement without any setup code.
// ON CONFLICT DO NOTHING makes retransmits idempotent by constraint — device gateways resend batches
// they are not sure were received, and a replayed row hits the hypertable's composite key.
app.MapPost("/readings", async (
    ReadingInput[] readings,
    NpgsqlDataSource db,
    ISqlRowMapper<ReadingRow> mapper,
    TimeProvider time,
    CancellationToken ct) => {
    if (readings.Length == 0) {
        return Results.BadRequest();
    }

    await using var connection = await db.OpenConnectionAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText =
        $"INSERT INTO {ReadingRowSqlMapper.TableName} ({ReadingRowSqlMapper.Columns.All}) "
        + $"VALUES ({ReadingRowSqlMapper.Columns.AllParameters}) ON CONFLICT DO NOTHING";
    var written = 0;
    foreach (var input in readings) {
        var row = new ReadingRow {
            DeviceId = input.DeviceId,
            Metric = input.Metric,
            // PostgreSQL timestamptz stores an instant; normalize whatever offset the device sent.
            RecordedAt = (input.RecordedAt ?? time.GetUtcNow()).ToUniversalTime(),
            Value = input.Value,
            Meta = input.Meta,
        };
        command.Parameters.Clear();
        mapper.BindParameters(command, row);
        written += await command.ExecuteNonQueryAsync(ct);
    }

    await transaction.CommitAsync(ct);
    return Results.Ok(new IngestResult(written));
});

// Latest sample for a device+metric — interpolated values bind as parameters, the :raw holes splice
// the generated constants, and the mapper materializes the row. Full SQL, no translation layer.
app.MapGet("/devices/{deviceId}/latest", async (
    string deviceId,
    string metric,
    NpgsqlDataSource db,
    ISqlRowMapper<ReadingRow> mapper,
    CancellationToken ct) => {
    await using var connection = await db.OpenConnectionAsync(ct);
    var reading = await connection.QueryFirstOrDefaultAsync(
        mapper,
        $"""
         SELECT {ReadingRowSqlMapper.Columns.All:raw} FROM {ReadingRowSqlMapper.TableName:raw}
         WHERE device_id = {deviceId} AND metric = {metric}
         ORDER BY recorded_at DESC LIMIT 1
         """,
        ct);

    return reading is null ? Results.NotFound() : Results.Ok(reading);
});

// Recent history, newest first.
app.MapGet("/devices/{deviceId}/history", async (
    string deviceId,
    string metric,
    NpgsqlDataSource db,
    ISqlRowMapper<ReadingRow> mapper,
    CancellationToken ct,
    int limit = 100) => {
    await using var connection = await db.OpenConnectionAsync(ct);
    var readings = await connection.QueryAsync(
        mapper,
        $"""
         SELECT {ReadingRowSqlMapper.Columns.All:raw} FROM {ReadingRowSqlMapper.TableName:raw}
         WHERE device_id = {deviceId} AND metric = {metric}
         ORDER BY recorded_at DESC LIMIT {limit}
         """,
        ct);

    return Results.Ok(readings);
});

// Hourly aggregate — the "full power of SQL" leg: TimescaleDB's time_bucket + plain aggregates stay
// SQL, and the MetricBucket mapper binds the projection by column name (a [SqlRecord] needs no
// physical table). Extension functions need no framework support — SQL stays hand-written.
app.MapGet("/devices/{deviceId}/stats", async (
    string deviceId,
    string metric,
    NpgsqlDataSource db,
    ISqlRowMapper<MetricBucket> mapper,
    TimeProvider time,
    CancellationToken ct,
    int hours = 24) => {
    var since = time.GetUtcNow().AddHours(-hours);
    await using var connection = await db.OpenConnectionAsync(ct);
    var buckets = await connection.QueryAsync(
        mapper,
        $"""
         SELECT time_bucket(INTERVAL '1 hour', recorded_at) AS bucket,
                count(*)   AS samples,
                avg(value) AS avg_value,
                min(value) AS min_value,
                max(value) AS max_value
         FROM {ReadingRowSqlMapper.TableName:raw}
         WHERE device_id = {deviceId} AND metric = {metric} AND recorded_at >= {since}
         GROUP BY 1
         ORDER BY 1
         """,
        ct);

    return Results.Ok(buckets);
});

app.Run();

// Exposes the entry point to WebApplicationFactory-based tests.
public partial class Program;
