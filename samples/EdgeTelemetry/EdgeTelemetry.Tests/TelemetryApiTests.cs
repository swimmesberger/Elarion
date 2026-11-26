using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using EdgeTelemetry.Api.Modules.Telemetry;
using EdgeTelemetry.Api.Modules.Telemetry.Handlers;
using Elarion.Abstractions;
using Elarion.Spreadsheets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        if (_factory is not null) await _factory.DisposeAsync();

        if (_container is not null) await _container.DisposeAsync();
    }

    [Fact]
    public async Task IngestThenQuery_RoundTripsThroughMigratedSchema() {
        Assert.SkipUnless(_factory is not null, _skipReason);

        // Handler tracing is always-on (the registration generator applies the ObservabilityDecorator as
        // the outermost decorator for every handler, attributes or not) — a listener on the Elarion and
        // Npgsql sources proves the dashboard's trace story: HTTP → handler span → SQL command span.
        var spans = new ConcurrentBag<(string Source, string Name)>();
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name is "Elarion.Handlers" or "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add((activity.Source.Name, activity.DisplayName))
        };
        ActivitySource.AddActivityListener(listener);

        using var client = _factory!.CreateClient();

        // Stream startup errors stay ordinary HTTP problems: SSE headers are not committed until the handler
        // accepts the request, so an invalid limit cannot turn into a half-open stream.
        using var invalidStream = await client.GetAsync(
            "/devices/edge-1/history/stream?metric=temperature&limit=0", Ct);
        invalidStream.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var missingLimit = await client.GetAsync(
            "/devices/edge-1/history/stream?metric=temperature", Ct);
        missingLimit.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var recordedAt = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        ReadingInput[] batch = [
            new() { DeviceId = "edge-1", Metric = "temperature", Value = 21.5, RecordedAt = recordedAt },
            new() {
                DeviceId = "edge-1", Metric = "temperature", Value = 23.0, RecordedAt = recordedAt.AddMinutes(30),
                Meta = new ReadingMeta { Unit = "°C", Source = "sensor-a", Quality = 3 }
            },
            new() { DeviceId = "edge-1", Metric = "humidity", Value = 40.0, RecordedAt = recordedAt.AddMinutes(5) },
            new() { DeviceId = "edge-2", Metric = "temperature", Value = 19.0, RecordedAt = recordedAt }
        ];

        var ingest =
            await client.PostAsJsonAsync("/readings", batch, TelemetryJsonContext.Default.ReadingInputArray, Ct);
        ingest.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ingest.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.IngestResult, Ct);
        result!.Written.Should().Be(4);

        // A retransmitted batch is idempotent by constraint: every row hits the hypertable's
        // composite key and ON CONFLICT DO NOTHING inserts nothing.
        var retransmit =
            await client.PostAsJsonAsync("/readings", batch, TelemetryJsonContext.Default.ReadingInputArray, Ct);
        var retransmitResult =
            await retransmit.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.IngestResult, Ct);
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

        // The export is finite SSE, not a buffered JSON array: consume it to completion and parse every row
        // through the same source-generated ReadingRow metadata as the rest of the sample.
        using var streamRequest = new HttpRequestMessage(
            HttpMethod.Get, "/devices/edge-1/history/stream?metric=temperature&limit=2");
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, Ct);
        streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        streamResponse.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var exported = new List<ReadingRow>();
        var streamLines = new List<string>();
        string? eventType = null;
        await using var stream = await streamResponse.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(Ct) is { } line) {
            streamLines.Add(line);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) {
                eventType = line["event: ".Length..];
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal)
                || eventType == "elarion.keepAlive"
                || line.Length == "data: ".Length) {
                if (line.Length == 0) eventType = null;
                continue;
            }

            var reading = JsonSerializer.Deserialize(line["data: ".Length..], TelemetryJsonContext.Default.ReadingRow);
            exported.Add(reading!);
        }

        exported.Select(reading => reading.Value).Should().Equal(23.0, 21.5);
        streamLines.Should().NotContain(line => line.StartsWith("id: ", StringComparison.Ordinal));

        // Hourly stats: the projection [SqlRecord] binds the aggregate SELECT.
        var stats = await client.GetFromJsonAsync(
            "/devices/edge-1/stats?metric=temperature&hours=87600", TelemetryJsonContext.Default.ListMetricBucket, Ct);
        stats.Should().ContainSingle();
        stats![0].Samples.Should().Be(2);
        stats[0].MinValue.Should().Be(21.5);
        stats[0].MaxValue.Should().Be(23.0);
        stats[0].AvgValue.Should().BeApproximately(22.25, 0.0001);

        // Every handler on the path emitted its span; the ingest ran its SQL inside one.
        var handlerSpans = spans.Where(s => s.Source == "Elarion.Handlers").Select(s => s.Name).ToList();
        handlerSpans.Should().Contain("handle IngestReadings");
        handlerSpans.Should().Contain("handle GetLatestReading");
        handlerSpans.Should().Contain("handle GetReadingHistory");
        handlerSpans.Should().Contain("stream ExportReadingHistory");
        handlerSpans.Should().Contain("handle GetMetricStats");
        spans.Should().Contain(s => s.Source == "Npgsql", "the SQL command spans nest under the handler spans");

        await SpreadsheetExportThenImport_RoundTripsReadingsAndRejectsABrokenRow(client);
    }

    // Part of the one test above: the canonical JSON accessor is installed once per process, so the class runs one host.
    private async Task SpreadsheetExportThenImport_RoundTripsReadingsAndRejectsABrokenRow(HttpClient client) {

        var recordedAt = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);
        foreach (var (value, minutes) in new[] { (1013.25, 0), (1009.5, 10) }) {
            ReadingInput[] reading = [
                new() { DeviceId = "sheet-1", Metric = "pressure", Value = value, RecordedAt = recordedAt.AddMinutes(minutes) }
            ];
            (await client.PostAsJsonAsync("/readings", reading, TelemetryJsonContext.Default.ReadingInputArray, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The export streams a workbook; reading it back through the registered reader gives the stored readings.
        using var export = await client.GetAsync("/devices/sheet-1/readings.xlsx?metric=pressure", Ct);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.Should().Be(SpreadsheetWorkbook.ContentType);
        var workbook = await export.Content.ReadAsByteArrayAsync(Ct);

        using (var scope = _factory!.Services.CreateScope()) {
            var reader = scope.ServiceProvider.GetRequiredService<SpreadsheetReader>();
            var read = await reader.ReadAsync<ReadingSheetRow>(new MemoryStream(workbook), cancellationToken: Ct);
            read.HasErrors.Should().BeFalse();
            read.Records.Select(record => (record.RecordedAt, record.Value))
                .Should().Equal((recordedAt, 1013.25), (recordedAt.AddMinutes(10), 1009.5));
        }

        // Importing a workbook stores its readings.
        var import = await ImportAsync(client, [
            new ReadingSheetRow { DeviceId = "sheet-2", Metric = "pressure", RecordedAt = recordedAt, Value = 1001 }
        ]);
        import.StatusCode.Should().Be(HttpStatusCode.OK);
        (await import.Content.ReadFromJsonAsync(TelemetryJsonContext.Default.IngestResult, Ct))!.Written.Should().Be(1);
        var imported = await client.GetFromJsonAsync(
            "/devices/sheet-2/latest?metric=pressure", TelemetryJsonContext.Default.ReadingRow, Ct);
        imported!.RecordedAt.Should().Be(new DateTimeOffset(recordedAt));
        imported.Value.Should().Be(1001);

        // A row that breaks the record's validation attributes rejects the file with a located problem.
        var broken = await ImportAsync(client, [
            new ReadingSheetRow { DeviceId = "sheet-3", Metric = "pressure", RecordedAt = recordedAt, Value = 1 },
            new ReadingSheetRow { DeviceId = new string('x', 65), Metric = "pressure", RecordedAt = recordedAt, Value = 2 }
        ]);
        broken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await broken.Content.ReadAsStringAsync(Ct)).Should().Contain("A3");
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, ReadingSheetRow[] rows) {
        using var content = new MemoryStream();
        await SpreadsheetWorkbook.WriteAsync(content, rows, cancellationToken: Ct);
        var command = new ImportReadingsSpreadsheet.Command {
            Workbook = new ElarionFile(content.ToArray(), SpreadsheetWorkbook.ContentType) { FileName = "readings.xlsx" }
        };

        return await client.PostAsJsonAsync(
            "/readings/import", command, TelemetryJsonContext.Default.ImportReadingsSpreadsheetCommand, Ct);
    }
}
