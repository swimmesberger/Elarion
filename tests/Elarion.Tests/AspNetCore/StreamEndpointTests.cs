using System.Net;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore.Streams;
using Elarion.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
