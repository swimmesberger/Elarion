using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using EdgeTelemetry.Api.Modules.Telemetry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace EdgeTelemetry.Tests;

/// <summary>
/// End-to-end over the real host against real PostgreSQL (Docker-gated; skips without a container
/// runtime): startup applies the embedded migrations, ingest binds through the generated mapper, and
/// the query endpoints read back through interpolated SQL — the whole EF-free tier in one pass.
/// The NativeAOT publish itself is verified out-of-band (see the README); under the test host the
/// same code runs JIT-compiled.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TelemetryApiTests : IAsyncLifetime {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;
    private string _skipReason = "";

    public async ValueTask InitializeAsync() {
        try {
            // The recipe image (ADR-0056): TimescaleDB rides the server image everywhere — dev,
            // Testcontainers, production — and the migration enables it like any other DDL.
            var container = new PostgreSqlBuilder("timescale/timescaledb:latest-pg17").Build();
            await container.StartAsync();
            _container = container;

            var connectionString = container.GetConnectionString() + ";Max Auto Prepare=16";
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("ConnectionStrings:Telemetry", connectionString));
        }
        catch (Exception ex) {
            _skipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() {
        if (_factory is not null) {
            await _factory.DisposeAsync();
        }

        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task IngestThenQuery_RoundTripsThroughMigratedSchema() {
        Assert.SkipUnless(_factory is not null, _skipReason);
        using var client = _factory!.CreateClient();

        var recordedAt = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        ReadingInput[] batch = [
            new() { DeviceId = "edge-1", Metric = "temperature", Value = 21.5, RecordedAt = recordedAt },
            new() {
                DeviceId = "edge-1", Metric = "temperature", Value = 23.0, RecordedAt = recordedAt.AddMinutes(30),
                Meta = new ReadingMeta { Unit = "°C", Source = "sensor-a", Quality = 3 },
            },
            new() { DeviceId = "edge-1", Metric = "humidity", Value = 40.0, RecordedAt = recordedAt.AddMinutes(5) },
            new() { DeviceId = "edge-2", Metric = "temperature", Value = 19.0, RecordedAt = recordedAt },
        ];

        var ingest = await client.PostAsJsonAsync("/readings", batch, TelemetryJsonContext.Default.ReadingInputArray, Ct);
        ingest.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ingest.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.IngestResult, Ct);
        result!.Written.Should().Be(4);

        // A retransmitted batch is idempotent by constraint: every row hits the hypertable's
        // composite key and ON CONFLICT DO NOTHING inserts nothing.
        var retransmit = await client.PostAsJsonAsync("/readings", batch, TelemetryJsonContext.Default.ReadingInputArray, Ct);
        var retransmitResult = await retransmit.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.IngestResult, Ct);
        retransmitResult!.Written.Should().Be(0);

        // Latest per device+metric: the mapper materializes the row, jsonb meta included.
        var latest = await client.GetFromJsonAsync(
            "/devices/edge-1/latest?metric=temperature", TelemetryJsonContext.Default.ReadingRow, Ct);
        latest!.Value.Should().Be(23.0);
        latest.Meta.Should().BeEquivalentTo(new ReadingMeta { Unit = "°C", Source = "sensor-a", Quality = 3 });

        var missing = await client.GetAsync("/devices/edge-9/latest?metric=temperature", Ct);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // History, newest first, limit honored.
        var history = await client.GetFromJsonAsync(
            "/devices/edge-1/history?metric=temperature&limit=1", TelemetryJsonContext.Default.ListReadingRow, Ct);
        history.Should().ContainSingle(reading => reading.Value == 23.0);

        // Hourly stats: the projection [SqlRecord] binds the aggregate SELECT.
        var stats = await client.GetFromJsonAsync(
            "/devices/edge-1/stats?metric=temperature&hours=87600", TelemetryJsonContext.Default.ListMetricBucket, Ct);
        stats.Should().ContainSingle();
        stats![0].Samples.Should().Be(2);
        stats[0].MinValue.Should().Be(21.5);
        stats[0].MaxValue.Should().Be(23.0);
        stats[0].AvgValue.Should().BeApproximately(22.25, 0.0001);
    }
}
