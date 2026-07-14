// .NET Aspire orchestration for the EdgeTelemetry sample: provisions TimescaleDB and runs the API
// against it — the single-click demo. `dotnet run --project samples/EdgeTelemetry/EdgeTelemetry.AppHost`
// brings up the database, injects its connection string (resource name "telemetry" →
// ConnectionStrings__telemetry; configuration keys are case-insensitive, so the API's
// GetConnectionString("Telemetry") resolves it), waits for readiness, and opens the Aspire dashboard.
// Requires a container runtime (Docker/Podman).
//
// The ADR-0056 posture in one line: TimescaleDB is the SAME Postgres resource with a different server
// image — the extension rides the image, and enabling it is the API's ordinary startup migration.
// The API itself deliberately stays the lean AOT host (no OpenTelemetry wiring), so the dashboard
// shows its console logs but no traces — this launcher is about the one-click dev loop, not APM.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage("timescale/timescaledb", "latest-pg17")
    .WithDataVolume();

var telemetryDb = postgres.AddDatabase("telemetry", databaseName: "edge_telemetry");

builder.AddProject<Projects.EdgeTelemetry_Api>("api")
    .WithReference(telemetryDb)
    .WaitFor(telemetryDb);

builder.Build().Run();
