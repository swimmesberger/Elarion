using System.Text.Json.Serialization;
using System.Threading.Channels;
using AwesomeAssertions;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Elarion.ClientEvents;
using Elarion.Connections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the client-events bridge: subscribe runs the shared fail-closed resolver (unauthenticated /
/// unknown-topic outcomes match the SSE endpoint's), a resolved subscription greets with
/// <c>elarion.connected</c> and then delivers published envelopes through the adapter callback, and a
/// connection's subscriptions die with the connection (the registry observer tie).
/// </summary>
public sealed partial class ClientConnectionEventBridgeTests {
    private sealed record InvoiceChanged : IClientEvent {
        public required Guid InvoiceId { get; init; }
    }

    [JsonSerializable(typeof(InvoiceChanged))]
    private sealed partial class BridgeTestJsonContext : JsonSerializerContext;

    [Fact]
    public async Task SubscribeAsync_Unauthenticated_IsRejected() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(new FakeCurrentUser("user-1", isAuthenticated: false));
        var bridge = provider.GetRequiredService<ClientConnectionEventBridge>();

        var result = await bridge.SubscribeAsync(
            Connection("conn-1", "user-1"), [new ClientEventSubscriptionRequest { Topic = "test.invoiceChanged" }],
            static (_, _) => ValueTask.CompletedTask, ct);

        result.Status.Should().Be(ClientEventSubscriptionStatus.Unauthenticated);
        result.Subscription.Should().BeNull();
    }

    [Fact]
    public async Task SubscribeAsync_UnknownTopic_IsNotFound() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(new FakeCurrentUser("user-1", isAuthenticated: true));
        var bridge = provider.GetRequiredService<ClientConnectionEventBridge>();

        var result = await bridge.SubscribeAsync(
            Connection("conn-1", "user-1"), [new ClientEventSubscriptionRequest { Topic = "test.doesNotExist" }],
            static (_, _) => ValueTask.CompletedTask, ct);

        result.Status.Should().Be(ClientEventSubscriptionStatus.NotFound);
    }

    [Fact]
    public async Task SubscribeAsync_DeliversGreetingThenPublishedEnvelopes() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(new FakeCurrentUser("user-1", isAuthenticated: true));
        var bridge = provider.GetRequiredService<ClientConnectionEventBridge>();
        var delivered = Channel.CreateUnbounded<ClientEventEnvelope>();

        var result = await bridge.SubscribeAsync(
            Connection("conn-1", "user-1"), [new ClientEventSubscriptionRequest { Topic = "test.invoiceChanged" }],
            (envelope, deliveryCt) => delivered.Writer.WriteAsync(envelope, deliveryCt), ct);
        result.Status.Should().Be(ClientEventSubscriptionStatus.Resolved);
        using var subscription = result.Subscription!;

        (await delivered.Reader.ReadAsync(ct)).Topic.Should().Be(ClientEventControlEvents.Connected);

        var invoiceId = Guid.CreateVersion7();
        await provider.GetRequiredService<IClientEventPublisher>()
            .PublishAsync(new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.Global, ct);

        var envelope = await delivered.Reader.ReadAsync(ct);
        envelope.Topic.Should().Be("test.invoiceChanged");
        envelope.Payload.Should().Contain(invoiceId.ToString());
    }

    [Fact]
    public async Task ConnectionUnregistration_DisposesItsSubscriptions() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider(new FakeCurrentUser("user-1", isAuthenticated: true));
        var bridge = provider.GetRequiredService<ClientConnectionEventBridge>();
        var registry = provider.GetRequiredService<IClientConnectionRegistry>();
        var sink = ClientConnectionRegistryTests.Sink("conn-1", "user-1");
        await registry.RegisterAsync(sink, ct);
        var delivered = Channel.CreateUnbounded<ClientEventEnvelope>();

        var result = await bridge.SubscribeAsync(
            sink.Connection, [new ClientEventSubscriptionRequest { Topic = "test.invoiceChanged" }],
            (envelope, deliveryCt) => delivered.Writer.WriteAsync(envelope, deliveryCt), ct);
        (await delivered.Reader.ReadAsync(ct)).Topic.Should().Be(ClientEventControlEvents.Connected);

        await registry.UnregisterAsync("conn-1", ct);
        await result.Subscription!.Completion.WaitAsync(ct);

        await provider.GetRequiredService<IClientEventPublisher>()
            .PublishAsync(new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Global, ct);
        delivered.Reader.Count.Should().Be(0);
    }

    private static ServiceProvider BuildProvider(ICurrentUser user) {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(BridgeTestJsonContext.Default));
        services.AddElarionClientEvents(events => events.AddTopic<InvoiceChanged>("test.invoiceChanged"));
        services.AddElarionConnections();
        services.AddScoped(_ => user);
        return services.BuildServiceProvider();
    }

    private static ClientConnection Connection(string connectionId, string userId) =>
        ClientConnectionRegistryTests.Sink(connectionId, userId).Connection;

    private sealed class FakeCurrentUser(string userId, bool isAuthenticated) : ICurrentUser {
        public string UserId => userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => isAuthenticated;
        public bool IsInRole(string role) => false;
    }
}
