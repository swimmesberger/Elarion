using EdgeTelemetry.Api;
using EdgeTelemetry.Api.Modules.Telemetry;
using EdgeTelemetry.Api.Modules.Telemetry.Handlers;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.Diagnostics;
using Elarion.Migrations.PostgreSql;
using Elarion.Sql;
using Elarion.Sql.PostgreSql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// EdgeTelemetry: the Elarion EF-free NativeAOT tier end-to-end. A telemetry ingest node published as
// a native binary — the use case is cold-start speed and footprint (edge boxes, scale-to-zero,
// sidecars, CLI-style lifetimes) — with the business logic in [Handler] classes like every Elarion
// app, and the data story carried by the AOT-tier pair:
//
//   Elarion.Migrations.PostgreSql (ADR-0057)  embedded V__ scripts, applied before the host is ready
//   Elarion.Sql                   (ADR-0058)  [SqlRecord] generated mappers + safe SQL interpolation
//
// No EF, no reflection, no runtime codegen: handler registrations come from the generated module
// bootstrapper, while mappers and JSON contracts are source-generated. The ordinary unary endpoints
// below are hand-authored in this compilation, so ASP.NET Core's Request Delegate Generator compiles
// their binding ahead of time (the ADR-0031 REST pattern). The streaming endpoint follows the same shape:
// a direct MapGet owns generated route/query binding, while ElarionHttpResults owns stream invocation,
// startup-error translation, and native SSE.

var builder = WebApplication.CreateSlimBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Telemetry")
                       ?? throw new InvalidOperationException("Missing connection string 'Telemetry'.");

// One central PostgreSQL data source for the EF-free tier — the shared core, EF Core's DbContext analogue:
// AddElarionPostgreSqlDataSource registers a single pooled NpgsqlDataSource plus the
// IElarionSqlDataSourceProvider the ISqlSession opens from, and the migration runner below borrows the same
// source, so the database is configured once. The slim builder maps exactly the PostgreSQL types this app
// uses (uuid, text, timestamptz, double precision, jsonb-as-string). Command LOGGING — the EF Core
// `LogTo`/`Database.Command` equivalent — is wired from the host's logger factory automatically, so every
// command logs its SQL under the "Npgsql.Command" category (visible in the Aspire dashboard's structured
// logs). Parameter VALUES stay out of logs unless EnableParameterLogging is set (the EnableSensitiveDataLogging
// analog, development only). A multi-tenant host would register its own IElarionSqlDataSourceProvider instead.
builder.Services.AddElarionPostgreSqlDataSource(
    connectionString,
    db => {
        if (builder.Environment.IsDevelopment()) db.EnableParameterLogging();
    });
builder.Services.AddSingleton(TimeProvider.System);

// The generated bootstrapper: registers the Telemetry module's handlers as typed
// IHandler<TRequest, Result<TResponse>> services and contributes the module's JSON context to the
// canonical options (ADR-0023). With no functional decorator attributes, only the always-on ObservabilityDecorator
// wraps them (ADR-0059: merged tracing + user-context enrichment — the Elarion.Handlers spans/metrics
// below, near-zero cost with no listener); gate decorators (authorization, validation, …) attach per attribute.
builder.Services.AddElarion(builder.Configuration);

// Mirror canonical JSON onto minimal-API binding — one JSON configuration for HTTP contracts, the
// Result<T> error envelope (it registers ProblemDetails for the RFC 7807 legs), and the [SqlJson]
// column, reflection-free.
builder.Services.AddElarionHttpJson();

// The generated per-assembly mapper registration (ISqlRowMapper<T> singletons; the ReadingRow mapper
// takes the canonical JSON accessor for its jsonb column).
builder.Services.AddElarionSqlMappers();

// The EF-free unit of work: registers the scoped ISqlSession handlers inject and the SqlUnitOfWork so
// command handlers (telemetry.ingest) commit their writes atomically through the framework transaction
// decorator — the AOT-tier counterpart to AddElarionUnitOfWork<TDbContext>(). The session opens from the
// IElarionSqlDataSourceProvider registered above.
builder.Services.AddElarionSqlUnitOfWork();

// Schema before traffic: the hosted service applies pending embedded migrations and fails startup on
// error — an edge node serving against a half-migrated schema is worse than one that does not start.
// The data-source-from-DI overload borrows the central NpgsqlDataSource registered above, so migrations
// and the access tier share one source (the runner borrows a connection and never disposes the source).
builder.Services.AddElarionPostgreSqlMigrations(
    options => options.AddScripts(typeof(Program).Assembly, "EdgeTelemetry.Api.Migrations."));

// Observability is composition too: when an OTLP endpoint is configured (the Aspire launcher injects
// OTEL_EXPORTER_OTLP_ENDPOINT), export Elarion handler spans/metrics, Npgsql command spans/metrics,
// ASP.NET Core server telemetry, and structured logs — a request on the dashboard reads
// HTTP → telemetry.ingest → npgsql INSERT, ADR-0033 user-context enrichment included. Without an
// endpoint — the bare edge binary — nothing registers, and the host stays lean.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) {
    builder.Logging.AddOpenTelemetry(logging => {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
    });
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(HandlerTelemetry.ActivitySourceName)
            .AddSource("Npgsql"))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(HandlerTelemetry.MeterName)
            .AddMeter("Npgsql"))
        .UseOtlpExporter();
}

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));

// Hand-authored, RDG-visible unary endpoints: each is one line of translation — bind, call the handler
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

// Unlike /history's buffered JSON array, this finite export leaves the Npgsql reader open while native SSE
// serializes each row. Direct MapGet keeps route/query binding visible to RDG; the lazy result starts the
// decorated stream only when ASP.NET executes it and can still return a normal problem before SSE headers.
app.MapGet("/devices/{deviceId}/history/stream", static (
        string deviceId,
        string metric,
        int limit) =>
    ElarionHttpResults.ToStreamResult<ExportReadingHistory.Query, ReadingRow>(
        new ExportReadingHistory.Query(deviceId, metric, limit)));

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
