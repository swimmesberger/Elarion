using EdgeTelemetry.Api;
using EdgeTelemetry.Api.Modules.Telemetry;
using EdgeTelemetry.Api.Modules.Telemetry.Handlers;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.Migrations.PostgreSql;
using Elarion.Sql;
using Npgsql;

// EdgeTelemetry: the Elarion EF-free NativeAOT tier end-to-end. A telemetry ingest node published as
// a native binary — the use case is cold-start speed and footprint (edge boxes, scale-to-zero,
// sidecars, CLI-style lifetimes) — with the business logic in [Handler] classes like every Elarion
// app, and the data story carried by the AOT-tier pair:
//
//   Elarion.Migrations.PostgreSql (ADR-0057)  embedded V__ scripts, applied before the host is ready
//   Elarion.Sql                   (ADR-0058)  [SqlRecord] generated mappers + safe SQL interpolation
//
// No EF, no reflection, no runtime codegen: handler registrations come from the generated module
// bootstrapper, mappers and JSON contracts are source-generated, and the HTTP endpoints below are
// hand-authored in this compilation so ASP.NET Core's Request Delegate Generator compiles the
// binding ahead of time too (the ADR-0031 REST pattern — a source generator cannot see another
// generator's output, so generated maps would fall back to the runtime delegate factory).

var builder = WebApplication.CreateSlimBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Telemetry")
    ?? throw new InvalidOperationException("Missing connection string 'Telemetry'.");

// One pooled data source for the host. The slim builder maps exactly the PostgreSQL types this app
// uses (uuid, text, timestamptz, double precision, jsonb-as-string); `Max Auto Prepare` turns the
// hot INSERT and SELECTs into prepared statements automatically after a few executions.
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString).Build();
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(TimeProvider.System);

// The generated bootstrapper: registers the Telemetry module's handlers as plain typed
// IHandler<TRequest, Result<TResponse>> services (no decorator attributes → no decorators) and
// contributes the module's JSON context to the canonical options (ADR-0023).
builder.Services.AddElarion(builder.Configuration);

// Mirror canonical JSON onto minimal-API binding — one JSON configuration for HTTP contracts, the
// Result<T> error envelope (it registers ProblemDetails for the RFC 7807 legs), and the [SqlJson]
// column, reflection-free.
builder.Services.AddElarionHttpJson();

// The generated per-assembly mapper registration (ISqlRowMapper<T> singletons; the ReadingRow mapper
// takes the canonical JSON accessor for its jsonb column).
builder.Services.AddElarionSqlMappers();

// Schema before traffic: the hosted service applies pending embedded migrations and fails startup on
// error — an edge node serving against a half-migrated schema is worse than one that does not start.
builder.Services.AddElarionPostgreSqlMigrations(
    dataSource,
    options => options.AddScripts(typeof(Program).Assembly, "EdgeTelemetry.Api.Migrations."));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

// Hand-authored, RDG-visible endpoints: each is one line of translation — bind, call the handler
// typed-directly, render the Result<T> (success → 200, AppError.Validation → 400,
// AppError.NotFound → 404 problem details) via ElarionHttpResults.
app.MapPost("/readings", static async (
    ReadingInput[] readings,
    IHandler<IngestReadings.Command, Result<IngestResult>> ingest,
    CancellationToken ct) =>
    ElarionHttpResults.ToResult(await ingest.HandleAsync(new IngestReadings.Command(readings), ct)));

app.MapGet("/devices/{deviceId}/latest", static async (
    string deviceId,
    string metric,
    IHandler<GetLatestReading.Query, Result<ReadingRow>> latest,
    CancellationToken ct) =>
    ElarionHttpResults.ToResult(await latest.HandleAsync(new GetLatestReading.Query(deviceId, metric), ct)));

app.MapGet("/devices/{deviceId}/history", static async (
    string deviceId,
    string metric,
    IHandler<GetReadingHistory.Query, Result<List<ReadingRow>>> history,
    CancellationToken ct,
    int limit = 100) =>
    ElarionHttpResults.ToResult(await history.HandleAsync(new GetReadingHistory.Query(deviceId, metric, limit), ct)));

app.MapGet("/devices/{deviceId}/stats", static async (
    string deviceId,
    string metric,
    IHandler<GetMetricStats.Query, Result<List<MetricBucket>>> stats,
    CancellationToken ct,
    int hours = 24) =>
    ElarionHttpResults.ToResult(await stats.HandleAsync(new GetMetricStats.Query(deviceId, metric, hours), ct)));

app.Run();

// Exposes the entry point to WebApplicationFactory-based tests.
public partial class Program;
