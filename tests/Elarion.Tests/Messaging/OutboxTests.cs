using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Coordination;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Serialization;
using Elarion.Idempotency;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Messaging;

public sealed record OutboxTestEvent(int Id, string Name) : IIntegrationEvent;

/// <summary>A canonical serializer for the outbox unit tests: reflection fallback so the plain test DTOs resolve.</summary>
internal static class OutboxTestJson {
    public static readonly IElarionJsonSerialization Instance =
        new ServiceCollection()
            .ConfigureElarionJson(o => o.EnableReflectionFallback = true)
            .BuildServiceProvider()
            .GetRequiredService<IElarionJsonSerialization>();
}

public sealed class OutboxIntegrationEventBusTests
{
    [Fact]
    public async Task PublishAsync_AppendsMessageWithoutSaving()
    {
        var store = new FakeOutboxStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var bus = new OutboxIntegrationEventBus(
            store, Catalog(Descriptor()), EmptyProvider.Instance,
            new OutboxOptions(), OutboxTestJson.Instance, time);

        await bus.PublishAsync(new OutboxTestEvent(7, "alice"), TestContext.Current.CancellationToken);

        var message = store.Appended.Should().ContainSingle().Subject;
        message.EventType.Should().Be(typeof(OutboxTestEvent).FullName);
        message.OccurredOnUtc.Should().Be(time.GetUtcNow());
        message.CorrelationId.Should().NotBe(Guid.Empty);
        var delivery = message.Deliveries.Should().ContainSingle().Subject;
        delivery.ConsumerId.Should().Be("consumer");
        delivery.OccurredOnUtc.Should().Be(message.OccurredOnUtc);

        var roundTripped = JsonSerializer.Deserialize<OutboxTestEvent>(message.Payload, OutboxTestJson.Instance.Options);
        roundTripped.Should().Be(new OutboxTestEvent(7, "alice"));
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        var bus = new OutboxIntegrationEventBus(
            new FakeOutboxStore(), Catalog(Descriptor()), EmptyProvider.Instance,
            new OutboxOptions(), OutboxTestJson.Instance, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await bus.PublishAsync<OutboxTestEvent>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishAsync_CreatesOneIndependentDeliveryPerConsumer_WithRoleAffinity() {
        var store = new FakeOutboxStore();
        var local = Descriptor() with { ConsumerId = "local" };
        var homed = Descriptor() with {
            ConsumerId = "homed",
            ResolveDeliveryRole = static (_, @event) =>
                ((OutboxTestEvent)@event).Id == 7 ? "actors:partition-3" : null
        };
        var bus = new OutboxIntegrationEventBus(
            store, Catalog(local, homed), EmptyProvider.Instance,
            new OutboxOptions(), OutboxTestJson.Instance, TimeProvider.System);

        await bus.PublishAsync(new OutboxTestEvent(7, "alice"), TestContext.Current.CancellationToken);

        var deliveries = store.Appended.Should().ContainSingle().Subject.Deliveries;
        deliveries.Should().HaveCount(2);
        deliveries.Single(delivery => delivery.ConsumerId == "local").TargetRole.Should().BeNull();
        deliveries.Single(delivery => delivery.ConsumerId == "homed").TargetRole
            .Should().Be("actors:partition-3");
    }

    [Fact]
    public async Task PublishAsync_WithoutRegisteredConsumer_FailsBeforePersisting() {
        var store = new FakeOutboxStore();
        var bus = new OutboxIntegrationEventBus(
            store, Catalog(), EmptyProvider.Instance,
            new OutboxOptions(), OutboxTestJson.Instance, TimeProvider.System);

        var act = async () => await bus.PublishAsync(
            new OutboxTestEvent(7, "alice"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no registered consumers*");
        store.Appended.Should().BeEmpty();
    }

    private static EventSubscriptionDescriptor Descriptor() => new() {
        ConsumerId = "consumer",
        EventType = typeof(OutboxTestEvent),
        Plane = EventPlane.Integration,
        ServiceType = typeof(object),
        InvokeAsync = static (_, _, _, _) => ValueTask.CompletedTask
    };

    private static OutboxConsumerCatalog Catalog(params EventSubscriptionDescriptor[] descriptors) =>
        new(descriptors);
}

public sealed class OutboxConsumerCatalogTests {
    [Fact]
    public void Constructor_IndexesAndOrdersConsumersOnce() {
        var second = Descriptor("second", order: 20);
        var first = Descriptor("first", order: 10);

        var catalog = new OutboxConsumerCatalog([second, first]);

        catalog.GetConsumers(typeof(OutboxTestEvent)).Select(descriptor => descriptor.ConsumerId)
            .Should().Equal("first", "second");
        catalog.TryGetConsumer("second", out var found).Should().BeTrue();
        found.Should().BeSameAs(second);
    }

    [Fact]
    public void Constructor_DuplicateConsumerId_Throws() {
        var act = () => new OutboxConsumerCatalog([Descriptor("same"), Descriptor("same")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*registered more than once*");
    }

    [Fact]
    public void Constructor_MissingConsumerId_Throws() {
        var act = () => new OutboxConsumerCatalog([Descriptor("")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no stable ConsumerId*");
    }

    private static EventSubscriptionDescriptor Descriptor(string consumerId, int order = 0) => new() {
        ConsumerId = consumerId,
        EventType = typeof(OutboxTestEvent),
        Plane = EventPlane.Integration,
        ServiceType = typeof(object),
        Order = order,
        InvokeAsync = static (_, _, _, _) => ValueTask.CompletedTask
    };
}

public sealed class OutboxEventDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_InvokesConsumerWithDeserializedEventAndCorrelation()
    {
        object? received = null;
        IEventContext? receivedContext = null;
        var dispatcher = CreateDispatcher((_, @event, context, _) =>
        {
            received = @event;
            receivedContext = context;
            return ValueTask.CompletedTask;
        });

        var correlationId = Guid.NewGuid();
        await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateDelivery(new OutboxTestEvent(7, "alice"), correlationId),
            TestContext.Current.CancellationToken);

        received.Should().Be(new OutboxTestEvent(7, "alice"));
        receivedContext!.CorrelationId.Should().Be(correlationId);
        receivedContext.Plane.Should().Be(EventPlane.Integration);
        // The generated subscriber delegate may cast the context to the strongly typed form.
        receivedContext.Should().BeAssignableTo<IEventContext<OutboxTestEvent>>();
    }

    [Fact]
    public async Task DispatchAsync_ExposesTheOutboxRowIdAsMessageId()
    {
        IEventContext? receivedContext = null;
        var dispatcher = CreateDispatcher((_, _, context, _) =>
        {
            receivedContext = context;
            return ValueTask.CompletedTask;
        });

        var delivery = CreateDelivery(new OutboxTestEvent(7, "alice"), Guid.NewGuid());
        await dispatcher.DispatchAsync(EmptyProvider.Instance, delivery, TestContext.Current.CancellationToken);

        // The durable, redelivery-stable identity a consumer keys downstream dedup on (ADR-0022) — distinct
        // from the correlation id, which is a tracing identifier.
        receivedContext!.MessageId.Should().Be(delivery.MessageId);
    }

    [Fact]
    public async Task DispatchAsync_SeedsTheMessageIdAsTheScopeIdempotencyKey()
    {
        // The inbox rail: the delivery scope's idempotency key is the outbox row id, so the Consumer-scoped
        // decorator on a handler-form consumer claims per (consumer, message).
        await using var provider = new ServiceCollection().AddElarionIdempotency().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        string? seededKey = null;
        var dispatcher = CreateDispatcher((sp, _, _, _) =>
        {
            sp.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out seededKey);
            return ValueTask.CompletedTask;
        });

        var delivery = CreateDelivery(new OutboxTestEvent(7, "alice"), Guid.NewGuid());
        await dispatcher.DispatchAsync(scope.ServiceProvider, delivery, TestContext.Current.CancellationToken);

        seededKey.Should().Be(delivery.MessageId.ToString("N"));
    }

    [Fact]
    public async Task DispatchAsync_ConsumerRuns_ReturnsDelivered()
    {
        var dispatcher = CreateDispatcher((_, _, _, _) => ValueTask.CompletedTask);

        var outcome = await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateDelivery(new OutboxTestEvent(1, "x"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        outcome.Should().Be(OutboxDispatchOutcome.Delivered);
    }

    [Fact]
    public async Task DispatchAsync_UnknownEventType_IsUnresolvableAndDoesNotInvokeConsumer()
    {
        var invoked = false;
        var dispatcher = CreateDispatcher((_, _, _, _) =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });

        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = "Some.Unregistered.Type",
            Payload = "{}",
            CorrelationId = Guid.NewGuid()
        };
        var outcome = await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateDelivery(message),
            TestContext.Current.CancellationToken);

        invoked.Should().BeFalse();
        outcome.Should().Be(OutboxDispatchOutcome.Unresolvable);
    }

    [Fact]
    public async Task DispatchAsync_PayloadDeserializesToNull_IsUnresolvable()
    {
        var invoked = false;
        var dispatcher = CreateDispatcher((_, _, _, _) =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        });

        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = typeof(OutboxTestEvent).FullName!,
            Payload = "null",
            CorrelationId = Guid.NewGuid()
        };
        var outcome = await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateDelivery(message),
            TestContext.Current.CancellationToken);

        invoked.Should().BeFalse();
        outcome.Should().Be(OutboxDispatchOutcome.Unresolvable);
    }

    [Fact]
    public async Task DispatchAsync_ConsumerThrows_Propagates()
    {
        var dispatcher = CreateDispatcher((_, _, _, _) => throw new InvalidOperationException("boom"));

        var act = async () => await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateDelivery(new OutboxTestEvent(1, "x"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    private static OutboxEventDispatcher CreateDispatcher(EventSubscriberInvokeDelegate invoke) =>
        new(
            new OutboxConsumerCatalog([
                new EventSubscriptionDescriptor
                {
                    ConsumerId = "consumer",
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = invoke
                }
            ]),
            new OutboxOptions(),
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);

    private static OutboxDelivery CreateDelivery(OutboxTestEvent @event, Guid correlationId) =>
        CreateDelivery(new OutboxMessage {
        Id = Guid.CreateVersion7(),
        OccurredOnUtc = DateTimeOffset.UnixEpoch,
        EventType = typeof(OutboxTestEvent).FullName!,
        Payload = JsonSerializer.Serialize(@event, OutboxTestJson.Instance.Options),
        CorrelationId = correlationId
    });

    private static OutboxDelivery CreateDelivery(OutboxMessage message) => new() {
        Id = Guid.CreateVersion7(),
        MessageId = message.Id,
        OccurredOnUtc = message.OccurredOnUtc,
        ConsumerId = "consumer",
        Message = message
    };
}

public sealed class OutboxDeliveryServiceTests
{
    [Fact]
    public async Task Delivers_PendingMessage_AndMarksProcessed()
    {
        var store = new FakeOutboxStore();
        store.Pending.Enqueue([Message(new OutboxTestEvent(1, "a"), out var id)]);

        await RunUntilSignaledAsync(store, new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) },
            new FakeTimeProvider(), recordingDispatcher: (_, _, _, _) => ValueTask.CompletedTask);

        store.Processed.Should().ContainSingle().Which.Id.Should().Be(id);
        store.Processed[0].LockId.Should().Be(store.LastLockId);
        store.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedConsumer_MarksFailedWithBackoffVisibilityTimeout()
    {
        var store = new FakeOutboxStore();
        var delivery = Message(new OutboxTestEvent(1, "a"), out var id);
        delivery.Attempts = 0;
        store.Pending.Enqueue([delivery]);
        var now = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(20),
            BaseRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromHours(1)
        };

        await RunUntilSignaledAsync(store, options, new FakeTimeProvider(now),
            recordingDispatcher: (_, _, _, _) => throw new InvalidOperationException("boom"));

        var failed = store.Failed.Should().ContainSingle().Subject;
        failed.Id.Should().Be(id);
        failed.LockId.Should().Be(store.LastLockId);
        // First attempt (attempts becomes 1): base delay × 2^0 = 5s.
        failed.RetryVisibleAfterUtc.Should().Be(now + TimeSpan.FromSeconds(5));
        store.Processed.Should().BeEmpty();
        store.PermanentlyFailed.Should().BeEmpty();
    }

    [Fact]
    public async Task UnresolvableEventType_IsParkedAndNotMarkedDelivered()
    {
        var store = new FakeOutboxStore();
        store.Pending.Enqueue(
        [
            DeliveryFor(new OutboxMessage {
                Id = Guid.NewGuid(),
                OccurredOnUtc = DateTimeOffset.UnixEpoch,
                EventType = "Some.Unregistered.Type",
                Payload = "{}",
                CorrelationId = Guid.NewGuid()
            })
        ]);

        await RunUntilSignaledAsync(store, new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) },
            new FakeTimeProvider(), recordingDispatcher: (_, _, _, _) => ValueTask.CompletedTask);

        store.PermanentlyFailed.Should().ContainSingle();
        store.Processed.Should().BeEmpty();
        store.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task LeaseLostOnFinalize_IsNoOp_NoRedeliveryLoop()
    {
        var store = new FakeOutboxStore { FinalizeSucceeds = false };
        store.Pending.Enqueue([Message(new OutboxTestEvent(1, "a"), out _)]);

        await RunUntilSignaledAsync(store, new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) },
            new FakeTimeProvider(), recordingDispatcher: (_, _, _, _) => ValueTask.CompletedTask);

        // MarkProcessed was attempted but returned false (lease lost); the service must not throw or loop.
        store.Processed.Should().BeEmpty();
    }

    [Fact]
    public async Task RoleBoundDelivery_WaitsUntilThisProcessHoldsTheRole()
    {
        var store = new FakeOutboxStore();
        var delivery = Message(new OutboxTestEvent(1, "a"), out var id);
        delivery = new OutboxDelivery {
            Id = delivery.Id,
            MessageId = delivery.MessageId,
            OccurredOnUtc = delivery.OccurredOnUtc,
            ConsumerId = delivery.ConsumerId,
            TargetRole = "actors",
            Message = delivery.Message
        };
        store.Pending.Enqueue([delivery]);
        var timeProvider = new FakeTimeProvider();
        var lease = new MutableRoleLease { Role = "actors" };
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(20)
        };

        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IRoleLeaseRegistry>(new FakeRoleLeaseRegistry(lease));
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxEventDispatcher(
            new OutboxConsumerCatalog([
                new EventSubscriptionDescriptor
                {
                    ConsumerId = "consumer",
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = (_, _, _, _) => ValueTask.CompletedTask
                }
            ]),
            options,
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);
        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            options,
            timeProvider,
            NullLogger<OutboxDeliveryService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => store.ClaimCalls >= 1);

        store.Pending.Should().HaveCount(1);
        store.Processed.Should().BeEmpty();

        lease.IsHeld = true;
        while (!store.Signal.Task.IsCompleted)
        {
            timeProvider.Advance(options.PollingInterval);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        store.Processed.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task RoleLostAfterBatchClaim_ReleasesRemainingDeliveryWithoutDispatchOrBackoff()
    {
        var store = new FakeOutboxStore();
        var first = WithTargetRole(Message(new OutboxTestEvent(1, "first"), out var firstId), "actors");
        var second = WithTargetRole(Message(new OutboxTestEvent(2, "second"), out var secondId), "actors");
        store.Pending.Enqueue([first, second]);
        var lease = new MutableRoleLease { Role = "actors", IsHeld = true };
        var calls = 0;

        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IRoleLeaseRegistry>(new FakeRoleLeaseRegistry(lease));
        await using var provider = services.BuildServiceProvider();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) };
        var dispatcher = new OutboxEventDispatcher(
            new OutboxConsumerCatalog([
                new EventSubscriptionDescriptor {
                    ConsumerId = "consumer",
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = (_, _, _, _) => {
                        calls++;
                        lease.IsHeld = false;
                        return ValueTask.CompletedTask;
                    }
                }
            ]),
            options,
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);
        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            options,
            new FakeTimeProvider(),
            NullLogger<OutboxDeliveryService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => store.Released.Count == 1);
        await service.StopAsync(TestContext.Current.CancellationToken);

        calls.Should().Be(1);
        store.Processed.Should().ContainSingle().Which.Should().Be((firstId, store.LastLockId));
        store.Released.Should().ContainSingle().Which.Should().Be((secondId, store.LastLockId));
        store.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task RetentionPurge_RunsOncePerPurgeInterval_NotEveryIdlePoll()
    {
        var store = new FakeOutboxStore();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromSeconds(1),
            PurgeInterval = TimeSpan.FromHours(1)
        };

        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxEventDispatcher(
            new OutboxConsumerCatalog([]), options, OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);
        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            options,
            timeProvider,
            NullLogger<OutboxDeliveryService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        // Several idle polls inside the purge interval: cycles run (claims observed) but no purge fires.
        await WaitUntilAsync(() => store.ClaimCalls >= 1);
        for (var i = 0; i < 3; i++)
        {
            var nextClaim = store.ClaimCalls + 1;
            timeProvider.Advance(options.PollingInterval);
            await WaitUntilAsync(() => store.ClaimCalls >= nextClaim);
        }

        store.PurgeCalls.Should().Be(0);

        // Crossing the purge interval runs exactly one purge.
        timeProvider.Advance(options.PurgeInterval);
        await WaitUntilAsync(() => store.PurgeCalls >= 1);

        // The next idle poll after a purge stays purge-free until another interval elapses.
        var claimsAfterPurge = store.ClaimCalls + 1;
        timeProvider.Advance(options.PollingInterval);
        await WaitUntilAsync(() => store.ClaimCalls >= claimsAfterPurge);
        store.PurgeCalls.Should().Be(1);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static async Task RunUntilSignaledAsync(
        FakeOutboxStore store,
        OutboxOptions options,
        FakeTimeProvider timeProvider,
        EventSubscriberInvokeDelegate recordingDispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxEventDispatcher(
            new OutboxConsumerCatalog([
                new EventSubscriptionDescriptor
                {
                    ConsumerId = "consumer",
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = recordingDispatcher
                }
            ]),
            options,
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);

        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            options,
            timeProvider,
            NullLogger<OutboxDeliveryService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static OutboxDelivery Message(OutboxTestEvent @event, out Guid id)
    {
        var message = new OutboxMessage {
            Id = Guid.CreateVersion7(),
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = typeof(OutboxTestEvent).FullName!,
            Payload = JsonSerializer.Serialize(@event, OutboxTestJson.Instance.Options),
            CorrelationId = Guid.NewGuid()
        };
        id = Guid.CreateVersion7();
        return new OutboxDelivery {
            Id = id,
            MessageId = message.Id,
            OccurredOnUtc = message.OccurredOnUtc,
            ConsumerId = "consumer",
            Message = message
        };
    }

    private static OutboxDelivery DeliveryFor(OutboxMessage message) => new() {
        Id = Guid.CreateVersion7(),
        MessageId = message.Id,
        OccurredOnUtc = message.OccurredOnUtc,
        ConsumerId = "consumer",
        Message = message
    };

    private static OutboxDelivery WithTargetRole(OutboxDelivery delivery, string targetRole) => new() {
        Id = delivery.Id,
        MessageId = delivery.MessageId,
        OccurredOnUtc = delivery.OccurredOnUtc,
        ConsumerId = delivery.ConsumerId,
        TargetRole = targetRole,
        Message = delivery.Message
    };
}

public sealed class OutboxServiceCollectionExtensionsTests
{
    [Fact]
    public void AddElarionOutbox_RegistersOutboxTier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new OutboxTestDbContext(new DbContextOptionsBuilder<OutboxTestDbContext>().Options));

        services.AddElarionOutbox<OutboxTestDbContext>(options => options.BatchSize = 5);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>().Should().BeOfType<OutboxIntegrationEventBus>();
        scope.ServiceProvider.GetRequiredService<IOutboxStore>().Should().BeOfType<EfCoreOutboxStore<OutboxTestDbContext>>();
        provider.GetRequiredService<OutboxOptions>().BatchSize.Should().Be(5);
        provider.GetServices<IHostedService>().Should().ContainSingle(service => service is OutboxDeliveryService);
    }

    [Fact]
    public void AddElarionOutbox_RunDeliveryWorkerDisabled_RegistersPublisherWithoutTheWorker()
    {
        // The publisher-only shape for heterogeneous topologies: a node with a feature module disabled still
        // publishes to the outbox, but must not claim messages whose consumers only exist on the worker node.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new OutboxTestDbContext(new DbContextOptionsBuilder<OutboxTestDbContext>().Options));

        services.AddElarionOutbox<OutboxTestDbContext>(options => options.RunDeliveryWorker = false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>().Should().BeOfType<OutboxIntegrationEventBus>();
        scope.ServiceProvider.GetRequiredService<IOutboxStore>().Should().BeOfType<EfCoreOutboxStore<OutboxTestDbContext>>();
        provider.GetServices<IHostedService>().Should().NotContain(service => service is OutboxDeliveryService);
    }

    private sealed class OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options) : DbContext(options);
}

internal sealed class FakeOutboxStore : IOutboxStore
{
    public List<OutboxMessage> Appended { get; } = [];
    public Queue<IReadOnlyList<OutboxDelivery>> Pending { get; } = new();
    public List<(Guid Id, Guid LockId)> Processed { get; } = [];
    public List<(Guid Id, Guid LockId)> Released { get; } = [];
    public List<(Guid Id, Guid LockId, string Error, DateTimeOffset RetryVisibleAfterUtc)> Failed { get; } = [];
    public List<(Guid Id, Guid LockId, string Error)> PermanentlyFailed { get; } = [];
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The lease id of the most recent claim, so tests can assert finalize was guarded on it.</summary>
    public Guid LastLockId { get; private set; }

    /// <summary>When <see langword="false"/>, every finalize call reports the lease was lost (returns false).</summary>
    public bool FinalizeSucceeds { get; init; } = true;

    private int _claimCalls;
    private int _purgeCalls;

    /// <summary>How many claim polls the delivery worker has run.</summary>
    public int ClaimCalls => Volatile.Read(ref _claimCalls);

    /// <summary>How many retention purges the delivery worker has run.</summary>
    public int PurgeCalls => Volatile.Read(ref _purgeCalls);

    public void Append(OutboxMessage message) => Appended.Add(message);

    public ValueTask<IReadOnlyList<OutboxDelivery>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        IReadOnlyCollection<string> heldRoles,
        CancellationToken ct)
    {
        LastLockId = lockId;
        Interlocked.Increment(ref _claimCalls);
        if (Pending.Count == 0) {
            return ValueTask.FromResult<IReadOnlyList<OutboxDelivery>>([]);
        }

        var next = Pending.Peek();
        if (next.Any(delivery => delivery.TargetRole is not null && !heldRoles.Contains(delivery.TargetRole))) {
            return ValueTask.FromResult<IReadOnlyList<OutboxDelivery>>([]);
        }

        return ValueTask.FromResult(Pending.Dequeue());
    }

    public ValueTask<bool> MarkProcessedAsync(Guid id, Guid lockId, DateTimeOffset processedOnUtc, CancellationToken ct)
    {
        if (FinalizeSucceeds)
        {
            Processed.Add((id, lockId));
        }

        Signal.TrySetResult();
        return ValueTask.FromResult(FinalizeSucceeds);
    }

    public ValueTask<bool> ReleaseClaimAsync(Guid id, Guid lockId, CancellationToken ct)
    {
        if (FinalizeSucceeds)
        {
            Released.Add((id, lockId));
        }

        Signal.TrySetResult();
        return ValueTask.FromResult(FinalizeSucceeds);
    }

    public ValueTask<bool> MarkFailedAsync(
        Guid id,
        Guid lockId,
        string error,
        DateTimeOffset retryVisibleAfterUtc,
        CancellationToken ct)
    {
        if (FinalizeSucceeds)
        {
            Failed.Add((id, lockId, error, retryVisibleAfterUtc));
        }

        Signal.TrySetResult();
        return ValueTask.FromResult(FinalizeSucceeds);
    }

    public ValueTask<bool> MarkPermanentlyFailedAsync(Guid id, Guid lockId, string error, CancellationToken ct)
    {
        if (FinalizeSucceeds)
        {
            PermanentlyFailed.Add((id, lockId, error));
        }

        Signal.TrySetResult();
        return ValueTask.FromResult(FinalizeSucceeds);
    }

    public ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        Interlocked.Increment(ref _purgeCalls);
        return ValueTask.FromResult(0);
    }
}

internal sealed class FakeRoleLeaseRegistry(params IRoleLease[] leases) : IRoleLeaseRegistry {
    public IReadOnlyCollection<IRoleLease> Leases { get; } = leases;
}

internal sealed class MutableRoleLease : IRoleLease {
    public required string Role { get; init; }
    public bool IsHeld { get; set; }
    public string? CurrentHolder => null;
}

internal sealed class EmptyProvider : IServiceProvider
{
    public static readonly EmptyProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
