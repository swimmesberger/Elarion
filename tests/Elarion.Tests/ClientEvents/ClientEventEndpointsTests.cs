using System.Net;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Elarion.ClientEvents;
using Elarion.ClientEvents.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.ClientEvents;

/// <summary>
/// End-to-end tests for the client-event SSE endpoint over a real Kestrel host: fail-closed subscribe-time
/// authorization (401 unauthenticated; 404 for unknown topics, failed topic requirements, and resource scopes
/// without a passing authorizer — never leaking a topic's existence) and the happy path of a published event
/// arriving as an <c>event:</c>/<c>data:</c> frame.
/// </summary>
public sealed partial class ClientEventEndpointsTests {
    private sealed record InvoiceChanged : IClientEvent {
        public required Guid InvoiceId { get; init; }
    }

    [JsonSerializable(typeof(InvoiceChanged))]
    private sealed partial class EndpointTestContext : JsonSerializerContext;

    private static string SubscriptionsUrl(string subscriptionsJson) =>
        "/events?subscriptions=" + Uri.EscapeDataString(subscriptionsJson);

    [Fact]
    public async Task Subscribe_Unauthenticated_Returns401() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("user-1", isAuthenticated: false));

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Subscribe_WithoutSubscriptions_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/events", ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_MalformedSubscriptions_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync(SubscriptionsUrl("not-json"), ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_UnknownTopic_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.doesNotExist"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_TopicRequirementDenied_Returns404() {
        // The topic requires a role; the fake authorizer denies. Denial reads as not found, like the
        // feature-gate convention: the topic's existence is what the 404 hides.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(
            ct,
            configureTopics: events => events.AddTopic<InvoiceChanged>(
                "test.invoiceChanged", t => t.RequireRole("admin")),
            authorizer: new FakeAuthorizer(allow: false));

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_TopicRequirement_WithoutAuthorizer_Returns404() {
        // Requirements beyond "authenticated" with no IAuthorizer registered fail closed.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(
            ct,
            configureTopics: events => events.AddTopic<InvoiceChanged>(
                "test.invoiceChanged", t => t.RequireRole("admin")));

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_ResourceScope_WithoutAuthorizer_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged","resource":"customer:42"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_ResourceScope_DeniedByAuthorizer_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(
            ct, subscriptionAuthorizer: new FakeSubscriptionAuthorizer(allowedResource: "customer:7"));

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged","resource":"customer:42"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Subscribe_ResourceScope_AllowAnyResource_SkipsAuthorizerAndDelivers() {
        // The topic declares its resource segment a routing key: no IClientEventSubscriptionAuthorizer is
        // registered and the resource-scoped subscription still opens and receives its events.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(
            ct,
            configureTopics: events => events.AddTopic<InvoiceChanged>(
                "test.invoiceChanged", t => t.AllowAnyResource()));

        using var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged","resource":"customer:42"}]"""),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var connected = await ReadFrameAsync(reader, ct);
        connected.Should().Contain("event: elarion.connected");

        var invoiceId = Guid.CreateVersion7();
        await host.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.Resource("customer:42"), ct);

        var frame = await ReadFrameAsync(reader, ct);
        frame.Should().ContainSingle(line => line.StartsWith("data: ") && line.Contains(invoiceId.ToString()));
    }

    [Fact]
    public async Task Subscribe_ResourceScope_AllowAnyResource_StillEnforcesTopicRequirements() {
        // AllowAnyResource opens the resource axis only; the topic's own requirements still gate the
        // subscription (here: a role with no IAuthorizer registered fails closed).
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(
            ct,
            configureTopics: events => events.AddTopic<InvoiceChanged>(
                "test.invoiceChanged", t => t.RequireRole("admin").AllowAnyResource()));

        var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged","resource":"customer:42"}]"""), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PublishedEvent_ArrivesAsSseFrame_OnUserScope() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged"}]"""),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var connected = await ReadFrameAsync(reader, ct);
        connected.Should().Contain("event: elarion.connected");

        var invoiceId = Guid.CreateVersion7();
        await host.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.User("user-1"), ct);

        var frame = await ReadFrameAsync(reader, ct);
        frame.Should().ContainSingle(line => line == "event: test.invoiceChanged");
        frame.Should().ContainSingle(line => line.StartsWith("data: ") && line.Contains(invoiceId.ToString()));
    }

    [Fact]
    public async Task PublishedEvent_ForAnotherUser_DoesNotArrive() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using var response = await host.Client.GetAsync(
            SubscriptionsUrl("""[{"topic":"test.invoiceChanged"}]"""),
            HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var connected = await ReadFrameAsync(reader, ct);
        connected.Should().Contain("event: elarion.connected");

        await host.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.User("someone-else"), ct);
        var globalId = Guid.CreateVersion7();
        await host.Publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = globalId }, ClientEventScope.Global, ct);

        // The next frame is the global event — the foreign user-scoped one was never written to this stream.
        var frame = await ReadFrameAsync(reader, ct);
        frame.Should().ContainSingle(line => line.StartsWith("data: ") && line.Contains(globalId.ToString()));
    }

    private static async Task<List<string>> ReadFrameAsync(StreamReader reader, CancellationToken ct) {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line) {
            if (line.Length == 0) {
                if (lines.Count > 0) {
                    break;
                }
                continue;
            }
            if (line.StartsWith(':')) {
                continue; // comments (connected/keep-alive) are not part of a frame
            }
            lines.Add(line);
        }
        return lines;
    }

    private static async Task<TestHost> StartAsync(
        CancellationToken cancellationToken,
        FakeCurrentUser? user = null,
        Action<ClientEventsBuilder>? configureTopics = null,
        IAuthorizer? authorizer = null,
        IClientEventSubscriptionAuthorizer? subscriptionAuthorizer = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(EndpointTestContext.Default));
        builder.Services.AddElarionClientEvents(configureTopics ?? (events =>
            events.AddTopic<InvoiceChanged>("test.invoiceChanged")));
        builder.Services.AddScoped<ICurrentUser>(_ => user ?? new FakeCurrentUser("user-1", isAuthenticated: true));
        if (authorizer is not null) {
            builder.Services.AddSingleton(authorizer);
        }
        if (subscriptionAuthorizer is not null) {
            builder.Services.AddSingleton(subscriptionAuthorizer);
        }

        var app = builder.Build();
        app.MapElarionClientEvents();
        await app.StartAsync(cancellationToken);

        var baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        return new TestHost(app, client);
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable {
        public HttpClient Client { get; } = client;

        public IClientEventPublisher Publisher => app.Services.GetRequiredService<IClientEventPublisher>();

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeCurrentUser(string userId, bool isAuthenticated) : ICurrentUser {
        public string UserId { get; } = userId;

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsAuthenticated { get; } = isAuthenticated;

        public bool IsInRole(string role) => false;
    }

    private sealed class FakeAuthorizer(bool allow) : IAuthorizer {
        public ValueTask<AppError?> AuthorizeAsync(
            AuthorizationRequirements requirements, object? resource, CancellationToken ct) =>
            ValueTask.FromResult(allow ? null : AppError.Forbidden("Denied."));
    }

    private sealed class FakeSubscriptionAuthorizer(string allowedResource) : IClientEventSubscriptionAuthorizer {
        public ValueTask<bool> AuthorizeAsync(ClientEventSubscription subscription, CancellationToken ct) =>
            ValueTask.FromResult(subscription.Scope.Value == allowedResource);
    }
}
