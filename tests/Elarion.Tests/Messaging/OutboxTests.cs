using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Idempotency;
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
        var bus = new OutboxIntegrationEventBus(store, new OutboxOptions(), OutboxTestJson.Instance, time);

        await bus.PublishAsync(new OutboxTestEvent(7, "alice"), TestContext.Current.CancellationToken);

        var message = store.Appended.Should().ContainSingle().Subject;
        message.EventType.Should().Be(typeof(OutboxTestEvent).FullName);
        message.OccurredOnUtc.Should().Be(time.GetUtcNow());
        message.CorrelationId.Should().NotBe(Guid.Empty);
        message.ProcessedOnUtc.Should().BeNull();

        var roundTripped = JsonSerializer.Deserialize<OutboxTestEvent>(message.Payload, OutboxTestJson.Instance.Options);
        roundTripped.Should().Be(new OutboxTestEvent(7, "alice"));
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        var bus = new OutboxIntegrationEventBus(new FakeOutboxStore(), new OutboxOptions(), OutboxTestJson.Instance, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await bus.PublishAsync<OutboxTestEvent>(null!, TestContext.Current.CancellationToken));
    }
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
            CreateMessage(new OutboxTestEvent(7, "alice"), correlationId),
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

        var message = CreateMessage(new OutboxTestEvent(7, "alice"), Guid.NewGuid());
        await dispatcher.DispatchAsync(EmptyProvider.Instance, message, TestContext.Current.CancellationToken);

        // The durable, redelivery-stable identity a consumer keys downstream dedup on (ADR-0022) — distinct
        // from the correlation id, which is a tracing identifier.
        receivedContext!.MessageId.Should().Be(message.Id);
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

        var message = CreateMessage(new OutboxTestEvent(7, "alice"), Guid.NewGuid());
        await dispatcher.DispatchAsync(scope.ServiceProvider, message, TestContext.Current.CancellationToken);

        seededKey.Should().Be(message.Id.ToString("N"));
    }

    [Fact]
    public async Task DispatchAsync_ConsumerRuns_ReturnsDelivered()
    {
        var dispatcher = CreateDispatcher((_, _, _, _) => ValueTask.CompletedTask);

        var outcome = await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateMessage(new OutboxTestEvent(1, "x"), Guid.NewGuid()),
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
        var outcome = await dispatcher.DispatchAsync(EmptyProvider.Instance, message, TestContext.Current.CancellationToken);

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
        var outcome = await dispatcher.DispatchAsync(EmptyProvider.Instance, message, TestContext.Current.CancellationToken);

        invoked.Should().BeFalse();
        outcome.Should().Be(OutboxDispatchOutcome.Unresolvable);
    }

    [Fact]
    public async Task DispatchAsync_ConsumerThrows_Propagates()
    {
        var dispatcher = CreateDispatcher((_, _, _, _) => throw new InvalidOperationException("boom"));

        var act = async () => await dispatcher.DispatchAsync(
            EmptyProvider.Instance,
            CreateMessage(new OutboxTestEvent(1, "x"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    private static OutboxEventDispatcher CreateDispatcher(EventSubscriberInvokeDelegate invoke) =>
        new(
            [
                new EventSubscriptionDescriptor
                {
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = invoke
                }
            ],
            new OutboxOptions(),
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);

    private static OutboxMessage CreateMessage(OutboxTestEvent @event, Guid correlationId) => new()
    {
        Id = Guid.NewGuid(),
        OccurredOnUtc = DateTimeOffset.UnixEpoch,
        EventType = typeof(OutboxTestEvent).FullName!,
        Payload = JsonSerializer.Serialize(@event, OutboxTestJson.Instance.Options),
        CorrelationId = correlationId
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
        var message = Message(new OutboxTestEvent(1, "a"), out var id);
        message.Attempts = 0;
        store.Pending.Enqueue([message]);
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
            new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OccurredOnUtc = DateTimeOffset.UnixEpoch,
                EventType = "Some.Unregistered.Type",
                Payload = "{}",
                CorrelationId = Guid.NewGuid()
            }
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
            [
                new EventSubscriptionDescriptor
                {
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = recordingDispatcher
                }
            ],
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

    private static OutboxMessage Message(OutboxTestEvent @event, out Guid id)
    {
        id = Guid.NewGuid();
        return new OutboxMessage
        {
            Id = id,
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = typeof(OutboxTestEvent).FullName!,
            Payload = JsonSerializer.Serialize(@event, OutboxTestJson.Instance.Options),
            CorrelationId = Guid.NewGuid()
        };
    }
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
    public Queue<IReadOnlyList<OutboxMessage>> Pending { get; } = new();
    public List<(Guid Id, Guid LockId)> Processed { get; } = [];
    public List<(Guid Id, Guid LockId, string Error, DateTimeOffset RetryVisibleAfterUtc)> Failed { get; } = [];
    public List<(Guid Id, Guid LockId, string Error)> PermanentlyFailed { get; } = [];
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The lease id of the most recent claim, so tests can assert finalize was guarded on it.</summary>
    public Guid LastLockId { get; private set; }

    /// <summary>When <see langword="false"/>, every finalize call reports the lease was lost (returns false).</summary>
    public bool FinalizeSucceeds { get; init; } = true;

    public void Append(OutboxMessage message) => Appended.Add(message);

    public ValueTask<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken ct)
    {
        LastLockId = lockId;
        return ValueTask.FromResult(Pending.Count > 0 ? Pending.Dequeue() : (IReadOnlyList<OutboxMessage>)[]);
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

    public ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) =>
        ValueTask.FromResult(0);
}

internal sealed class EmptyProvider : IServiceProvider
{
    public static readonly EmptyProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
