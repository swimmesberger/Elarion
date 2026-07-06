using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;
using Elarion.ClientEvents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.ClientEvents;

/// <summary>
/// Covers the client-event runtime: the catalog (opt-in by enumeration, duplicate rejection), the publisher
/// (topic resolution + canonical-JSON payload), and the in-process registry (exact topic+scope matching,
/// audience isolation, bounded drop-oldest buffering, unsubscribe on dispose).
/// </summary>
public sealed partial class ClientEventRuntimeTests {
    private sealed record InvoiceChanged : IClientEvent {
        public required Guid InvoiceId { get; init; }
    }

    private sealed record OrderChanged : IClientEvent {
        public required Guid OrderId { get; init; }
    }

    [JsonSerializable(typeof(InvoiceChanged))]
    [JsonSerializable(typeof(OrderChanged))]
    private sealed partial class ClientEventTestContext : JsonSerializerContext;

    private static ServiceProvider BuildProvider(Action<ClientEventsBuilder>? configure = null) {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(ClientEventTestContext.Default));
        services.AddElarionClientEvents(configure ?? (events =>
            events.AddTopic<InvoiceChanged>("test.invoiceChanged")));
        return services.BuildServiceProvider();
    }

    private static ClientEventSubscription Subscription(string topic, ClientEventScope scope) =>
        new() { Topic = topic, Scope = scope };

    [Fact]
    public async Task DeliverToAll_ReachesEverySubscriberRegardlessOfSubscriptions() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var delivery = provider.GetRequiredService<IClientEventLocalDelivery>();
        using var userScoped = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.User("user-1"))]);
        using var global = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        delivery.DeliverToAll(new ClientEventEnvelope {
            Id = Guid.CreateVersion7(),
            Topic = ClientEventControlEvents.Connected,
            Scope = ClientEventScope.Global,
            Payload = "{}",
        });

        (await userScoped.Events.ReadAsync(ct)).Topic.Should().Be(ClientEventControlEvents.Connected);
        (await global.Events.ReadAsync(ct)).Topic.Should().Be(ClientEventControlEvents.Connected);
    }

    [Fact]
    public async Task Publish_ReachesMatchingUserScopeSubscriber_WithSerializedPayload() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        using var handle = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.User("user-1"))]);

        var invoiceId = Guid.CreateVersion7();
        await publisher.PublishAsync(new InvoiceChanged { InvoiceId = invoiceId }, ClientEventScope.User("user-1"), ct);

        handle.Events.TryRead(out var envelope).Should().BeTrue();
        envelope!.Topic.Should().Be("test.invoiceChanged");
        envelope.Scope.Should().Be(ClientEventScope.User("user-1"));
        envelope.Payload.Should().Contain(invoiceId.ToString());
    }

    [Fact]
    public async Task Publish_UserScope_IsInvisibleToOtherUsersAndGlobalSubscribers() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        using var otherUser = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.User("user-2"))]);
        using var global = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        await publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.User("user-1"), ct);

        otherUser.Events.TryRead(out _).Should().BeFalse();
        global.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Publish_ResourceScope_ReachesExactlyThatResource() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        using var customer42 = source.Subscribe([
            Subscription("test.invoiceChanged", ClientEventScope.Resource("customer:42"))]);
        using var customer7 = source.Subscribe([
            Subscription("test.invoiceChanged", ClientEventScope.Resource("customer:7"))]);

        await publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Resource("customer:42"), ct);

        customer42.Events.TryRead(out _).Should().BeTrue();
        customer7.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Publish_GlobalScope_ReachesGlobalSubscribers() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        using var handle = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        await publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Global, ct);

        handle.Events.TryRead(out _).Should().BeTrue();
    }

    [Fact]
    public async Task Publish_UnregisteredEventType_FailsLoud() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();

        var publish = async () => await publisher.PublishAsync(
            new OrderChanged { OrderId = Guid.CreateVersion7() }, ClientEventScope.Global, ct);

        (await publish.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not registered as a client-event topic*");
    }

    [Fact]
    public async Task Registrations_Compose_AcrossMultipleAddCalls() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(ClientEventTestContext.Default));
        services.AddElarionClientEvents(events => events.AddTopic<InvoiceChanged>("test.invoiceChanged"));
        services.AddElarionClientEvents(events => events.AddTopic<OrderChanged>("test.orderChanged"));
        await using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<ClientEventTopicCatalog>();

        catalog.FindByName("test.invoiceChanged").Should().NotBeNull();
        catalog.FindByName("test.orderChanged").Should().NotBeNull();
    }

    [Fact]
    public async Task DuplicateTopicName_IsRejected() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(ClientEventTestContext.Default));
        services.AddElarionClientEvents(events => events.AddTopic<InvoiceChanged>("test.invoiceChanged"));
        services.AddElarionClientEvents(events => events.AddTopic<OrderChanged>("test.invoiceChanged"));
        await using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<ClientEventTopicCatalog>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*declared more than once*");
    }

    [Fact]
    public async Task DuplicateEventType_IsRejected() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(ClientEventTestContext.Default));
        services.AddElarionClientEvents(events => events
            .AddTopic<InvoiceChanged>("test.invoiceChanged")
            .AddTopic<InvoiceChanged>("test.invoiceChangedAgain"));
        await using var provider = services.BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<ClientEventTopicCatalog>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*more than one topic*");
    }

    [Fact]
    public async Task SlowSubscriber_DropsOldestInsteadOfStallingDelivery() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        using var handle = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);

        var firstId = Guid.CreateVersion7();
        await publisher.PublishAsync(new InvoiceChanged { InvoiceId = firstId }, ClientEventScope.Global, ct);
        for (var i = 0; i < 80; i++) {
            await publisher.PublishAsync(
                new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Global, ct);
        }

        var received = 0;
        var sawFirst = false;
        while (handle.Events.TryRead(out var envelope)) {
            received++;
            sawFirst |= envelope.Payload.Contains(firstId.ToString());
        }

        received.Should().BeLessThanOrEqualTo(64);
        sawFirst.Should().BeFalse();
    }

    [Fact]
    public async Task DisposedSubscription_StopsReceivingAndCompletes() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var publisher = provider.GetRequiredService<IClientEventPublisher>();
        var handle = source.Subscribe([Subscription("test.invoiceChanged", ClientEventScope.Global)]);
        handle.Dispose();

        await publisher.PublishAsync(
            new InvoiceChanged { InvoiceId = Guid.CreateVersion7() }, ClientEventScope.Global, ct);

        handle.Events.TryRead(out _).Should().BeFalse();
        handle.Events.Completion.IsCompleted.Should().BeTrue();
    }
}
