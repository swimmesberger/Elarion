using System.Net;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Streams;
using Elarion.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// The ADR-0052 SSE leg over a real Kestrel host: elements arrive as SSE events in publish order with
/// <c>id:</c> = sequence, <c>Last-Event-ID</c> (and <c>?after=</c>) resume from the hub's ring, an
/// unknown stream is 404, and completing the hub ends the response.
/// </summary>
public sealed class StreamEndpointTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamsRetainedElements_InOrder_WithSequenceIds() {
        var hub = new StreamHub<TestQuote>(new StreamHubOptions { ReplayCapacity = 16 });
        await PublishAsync(hub, 100m, 101m, 102m);
        await using var host = await StartAsync(hub);

        var events = await ReadEventsAsync(host, "/stream/ELN", count: 3);

        events.Select(static e => e.Id).Should().Equal("1", "2", "3");
        events[0].Data.Should().Contain("\"price\":100").And.Contain("\"symbol\":\"ELN\"");
        events[2].Data.Should().Contain("\"price\":102");
    }

    [Fact]
    public async Task LastEventIdHeader_ResumesAfterTheSequence() {
        var hub = new StreamHub<TestQuote>(new StreamHubOptions { ReplayCapacity = 16 });
        await PublishAsync(hub, 100m, 101m, 102m, 103m, 104m);
        await using var host = await StartAsync(hub);

        var events = await ReadEventsAsync(host, "/stream/ELN", count: 2, lastEventId: "3");

        events.Select(static e => e.Id).Should().Equal("4", "5");
    }

    [Fact]
    public async Task AfterQuery_IsTheManualResumeForm() {
        var hub = new StreamHub<TestQuote>(new StreamHubOptions { ReplayCapacity = 16 });
        await PublishAsync(hub, 100m, 101m, 102m);
        await using var host = await StartAsync(hub);

        var events = await ReadEventsAsync(host, "/stream/ELN?after=2", count: 1);

        events.Select(static e => e.Id).Should().Equal("3");
    }

    [Fact]
    public async Task UnknownStream_Is404() {
        var hub = new StreamHub<TestQuote>();
        await using var host = await StartAsync(hub);

        var response = await host.Client.GetAsync("/stream/missing", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CompletingTheHub_EndsTheResponse() {
        var hub = new StreamHub<TestQuote>(new StreamHubOptions { ReplayCapacity = 16 });
        await PublishAsync(hub, 100m);
        hub.Complete();
        await using var host = await StartAsync(hub);

        // Reading the whole body must terminate: replay, then the completed hub closes the stream.
        using var response = await host.Client.GetAsync(
            "/stream/ELN", HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.Should().Contain("id: 1").And.Contain("\"price\":100");
    }

    [Fact]
    public async Task RequestDrivenStream_MapsUpfrontFailureBeforeSseHeaders() {
        await using var host = await StartRequestDrivenAsync(reject: true);

        using var response = await host.Client.GetAsync("/export", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task RequestDrivenStream_WritesCanonicalItemsAndCompletes() {
        await using var host = await StartRequestDrivenAsync(reject: false);

        using var response = await host.Client.GetAsync("/export", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Be("data: {\"symbol\":\"ELN\",\"price\":100}\n\n");
    }

    [Fact]
    public async Task RequestDrivenStream_IndentedCanonicalJsonPrefixesEverySseDataLine() {
        await using var host = await StartRequestDrivenAsync(reject: false, writeIndented: true);

        using var response = await host.Client.GetAsync("/export", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().OnlyContain(line => line.StartsWith("data: ", StringComparison.Ordinal));
        // EventSource joins data lines with LF before delivering the event's data payload.
        var reassembled = string.Join("\n", body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line["data: ".Length..]));
        reassembled.Should().Be("{\n  \"symbol\": \"ELN\",\n  \"price\": 100\n}");
    }

    [Fact]
    public async Task RequestDrivenStream_DoesNotStartWhenAnEndpointFilterReplacesTheResult() {
        var starts = new HandlerStarts();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(starts);
        builder.Services.AddScoped<IStreamHandler<ExportRequest, TestQuote>, CountingExportHandler>();
        var app = builder.Build();
        app.MapGet("/export", static () =>
                ElarionHttpResults.ToStreamResult<ExportRequest, TestQuote>(new ExportRequest()))
            .AddEndpointFilter(static async (context, next) => {
                _ = await next(context);
                return Results.NoContent();
            });
        await app.StartAsync(Ct);
        await using var host = new TestHost(app);

        using var response = await host.Client.GetAsync("/export", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        starts.Count.Should().Be(0);
    }

    [Fact]
    public async Task RequestDrivenStreamEvents_EmitKeepAliveWhileOneMoveNextRemainsPending() {
        var time = new NotifyingFakeTimeProvider();
        var gate = new BlockingExportGate();
        await using var enumerator = StreamEndpointRouteBuilderExtensions.StreamHandlerEventsAsync(
            new BlockingExportStream(gate),
            StreamEndpointTestContext.Default.TestQuote,
            time,
            Ct).GetAsyncEnumerator(Ct);
        var moving = enumerator.MoveNextAsync().AsTask();
        try {
            await gate.Entered.Task.WaitAsync(Ct);
            await time.TimerCreated.Task.WaitAsync(Ct);
            time.Advance(TimeSpan.FromSeconds(15));
            (await moving.WaitAsync(Ct)).Should().BeTrue();
            enumerator.Current.EventType.Should().Be("elarion.keepAlive");
            enumerator.Current.Data.Should().BeEmpty();

            gate.Release.TrySetResult();
            (await enumerator.MoveNextAsync()).Should().BeFalse();
            gate.MoveNextCount.Should().Be(1);
            gate.DisposeCount.Should().Be(1);
        } finally {
            // Always release the blocked source so a failed assertion cannot leave the writer running.
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task RequestDrivenStreamEvents_CancellationSettlesPendingMoveBeforeDisposal() {
        var gate = new SerializedCleanupGate();
        using var cancelled = new CancellationTokenSource();
        await using var enumerator = StreamEndpointRouteBuilderExtensions.StreamHandlerEventsAsync(
            new SerializedCleanupStream(gate), StreamEndpointTestContext.Default.TestQuote,
            TimeProvider.System, cancelled.Token).GetAsyncEnumerator(cancelled.Token);
        var moving = enumerator.MoveNextAsync().AsTask();

        await gate.Entered.Task.WaitAsync(Ct);
        cancelled.Cancel();
        (await moving.WaitAsync(Ct)).Should().BeFalse();
        await enumerator.DisposeAsync();

        gate.MoveNextCount.Should().Be(1);
        gate.DisposeCount.Should().Be(1);
        gate.ConcurrentDisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RequestDrivenStreamEvents_ConsumerFailureSettlesPendingMoveBeforeDisposal() {
        var gate = new SerializedCleanupGate();
        var time = new NotifyingFakeTimeProvider();
        var enumerator = StreamEndpointRouteBuilderExtensions.StreamHandlerEventsAsync(
            new SerializedCleanupStream(gate), StreamEndpointTestContext.Default.TestQuote,
            time, Ct).GetAsyncEnumerator(Ct);
        var moving = enumerator.MoveNextAsync().AsTask();

        await gate.Entered.Task.WaitAsync(Ct);
        await time.TimerCreated.Task.WaitAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(15));
        (await moving.WaitAsync(Ct)).Should().BeTrue();
        await enumerator.DisposeAsync();

        gate.MoveNextCount.Should().Be(1);
        gate.DisposeCount.Should().Be(1);
        gate.ConcurrentDisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RequestDrivenStreamEvents_TimerFailureSettlesPendingMoveBeforeDisposal() {
        var gate = new SerializedCleanupGate();
        await using var enumerator = StreamEndpointRouteBuilderExtensions.StreamHandlerEventsAsync(
            new SerializedCleanupStream(gate), StreamEndpointTestContext.Default.TestQuote,
            new ThrowingTimeProvider(), Ct).GetAsyncEnumerator(Ct);

        Func<Task> act = () => enumerator.MoveNextAsync().AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("timer failure");

        gate.MoveNextCount.Should().Be(1);
        gate.DisposeCount.Should().Be(1);
        gate.ConcurrentDisposeCount.Should().Be(0);
    }

    private static async Task PublishAsync(StreamHub<TestQuote> hub, params decimal[] prices) {
        foreach (var price in prices) {
            await hub.PublishAsync(new TestQuote { Symbol = "ELN", Price = price }, Ct);
        }
    }

    private static async Task<TestHost> StartAsync(StreamHub<TestQuote> hub) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionJson();
        builder.Services.AddElarionHttpJson();
        builder.Services.ConfigureElarionJson(static options =>
            options.TypeInfoResolvers.Add(StreamEndpointTestContext.Default));

        var app = builder.Build();
        app.MapElarionStream<TestQuote>("/stream/{symbol}", (context, after) =>
            context.Request.RouteValues["symbol"] as string == "ELN"
                ? hub.SubscribeSequenced(new StreamSubscribeOptions {
                    Replay = StreamReplay.Available, ResumeAfterSequence = after,
                })
                : null);
        await app.StartAsync(Ct);
        return new TestHost(app);
    }

    private static async Task<TestHost> StartRequestDrivenAsync(bool reject, bool writeIndented = false) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddElarionJson();
        builder.Services.AddElarionHttpJson();
        builder.Services.ConfigureElarionJson(options => {
            options.TypeInfoResolvers.Add(StreamEndpointTestContext.Default);
            if (writeIndented)
                options.PostConfigure = json => json.WriteIndented = true;
        });
        builder.Services.AddScoped<IStreamHandler<ExportRequest, TestQuote>>(_ => new ExportHandler(reject));
        var app = builder.Build();
        app.MapGet("/export", static () =>
            ElarionHttpResults.ToStreamResult<ExportRequest, TestQuote>(new ExportRequest()));
        await app.StartAsync(Ct);
        return new TestHost(app);
    }

    private static async Task<List<(string Id, string Data)>> ReadEventsAsync(
        TestHost host, string path, int count, string? lastEventId = null) {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (lastEventId is not null) {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var events = new List<(string Id, string Data)>();
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        string? id = null;
        string? data = null;
        while (events.Count < count && await reader.ReadLineAsync(Ct) is { } line) {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) {
                id = line["id: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) {
                data = line["data: ".Length..];
            }
            else if (line.Length == 0 && id is not null && data is not null) {
                events.Add((id, data));
                (id, data) = (null, null);
            }
        }

        return events;
    }

    public sealed record TestQuote {
        [JsonPropertyName("symbol")]
        public required string Symbol { get; init; }

        [JsonPropertyName("price")]
        public required decimal Price { get; init; }
    }

    private sealed record ExportRequest;

    private sealed class ExportHandler(bool reject) : IStreamHandler<ExportRequest, TestQuote> {
        public ValueTask<Result<IAsyncEnumerable<TestQuote>>> HandleAsync(ExportRequest request, CancellationToken ct) =>
            reject
                ? ValueTask.FromResult<Result<IAsyncEnumerable<TestQuote>>>(AppError.NotFound("missing"))
                : ValueTask.FromResult(Result<IAsyncEnumerable<TestQuote>>.Success(Items()));

        private static async IAsyncEnumerable<TestQuote> Items() {
            yield return new TestQuote { Symbol = "ELN", Price = 100m };
            await Task.Yield();
        }
    }

    private sealed class HandlerStarts {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Record() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingExportHandler(HandlerStarts starts) : IStreamHandler<ExportRequest, TestQuote> {
        public ValueTask<Result<IAsyncEnumerable<TestQuote>>> HandleAsync(
            ExportRequest request,
            CancellationToken ct) {
            starts.Record();
            return ValueTask.FromResult(Result<IAsyncEnumerable<TestQuote>>.Success(Items()));
        }

        private static async IAsyncEnumerable<TestQuote> Items() {
            yield return new TestQuote { Symbol = "ELN", Price = 100m };
            await Task.Yield();
        }
    }

    private sealed class BlockingExportGate {
        private int _disposeCount;
        private int _moveNextCount;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount => _disposeCount;
        public int MoveNextCount => _moveNextCount;
        public void MoveNextCalled() => Interlocked.Increment(ref _moveNextCount);
        public void Disposed() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class BlockingExportStream(BlockingExportGate gate) : IAsyncEnumerable<TestQuote> {
        public IAsyncEnumerator<TestQuote> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(gate, cancellationToken);

        private sealed class Enumerator(BlockingExportGate gate, CancellationToken cancellationToken) : IAsyncEnumerator<TestQuote> {
            public TestQuote Current => throw new InvalidOperationException("The blocked stream does not yield items.");

            public async ValueTask<bool> MoveNextAsync() {
                gate.MoveNextCalled();
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                return false;
            }

            public ValueTask DisposeAsync() {
                gate.Disposed();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SerializedCleanupGate {
        private int _activeMoves;
        private int _concurrentDisposeCount;
        private int _disposeCount;
        private int _moveNextCount;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConcurrentDisposeCount => _concurrentDisposeCount;
        public int DisposeCount => _disposeCount;
        public int MoveNextCount => _moveNextCount;
        public void MoveStarted() {
            Interlocked.Increment(ref _moveNextCount);
            Interlocked.Increment(ref _activeMoves);
            Entered.TrySetResult();
        }
        public void MoveEnded() => Interlocked.Decrement(ref _activeMoves);
        public void Disposed() {
            if (Volatile.Read(ref _activeMoves) != 0)
                Interlocked.Increment(ref _concurrentDisposeCount);
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class SerializedCleanupStream(SerializedCleanupGate gate) : IAsyncEnumerable<TestQuote> {
        public IAsyncEnumerator<TestQuote> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(gate, cancellationToken);

        private sealed class Enumerator(SerializedCleanupGate gate, CancellationToken cancellationToken) : IAsyncEnumerator<TestQuote> {
            public TestQuote Current => throw new InvalidOperationException("The blocked stream does not yield items.");

            public async ValueTask<bool> MoveNextAsync() {
                gate.MoveStarted();
                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return false;
                } finally {
                    gate.MoveEnded();
                }
            }

            public ValueTask DisposeAsync() {
                gate.Disposed();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("timer failure");
    }

    private sealed class NotifyingFakeTimeProvider : TimeProvider {
        private readonly FakeTimeProvider _inner = new();
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Advance(TimeSpan delta) => _inner.Advance(delta);
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            TimerCreated.TrySetResult();
            return _inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class TestHost(WebApplication app) : IAsyncDisposable {
        public HttpClient Client { get; } = new() {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First())
        };

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}

[JsonSerializable(typeof(StreamEndpointTests.TestQuote))]
internal sealed partial class StreamEndpointTestContext : JsonSerializerContext;
