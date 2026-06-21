using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Messaging;

public sealed record OutboxTestEvent(int Id, string Name) : IIntegrationEvent;

public sealed class OutboxIntegrationEventBusTests
{
    [Fact]
    public async Task PublishAsync_AppendsMessageWithoutSaving()
    {
        var store = new FakeOutboxStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        var bus = new OutboxIntegrationEventBus(store, new OutboxOptions(), time);

        await bus.PublishAsync(new OutboxTestEvent(7, "alice"), TestContext.Current.CancellationToken);

        var message = store.Appended.Should().ContainSingle().Subject;
        message.EventType.Should().Be(typeof(OutboxTestEvent).FullName);
        message.OccurredOnUtc.Should().Be(time.GetUtcNow());
        message.CorrelationId.Should().NotBe(Guid.Empty);
        message.ProcessedOnUtc.Should().BeNull();

        var roundTripped = JsonSerializer.Deserialize<OutboxTestEvent>(message.Payload, new OutboxOptions().SerializerOptions);
        roundTripped.Should().Be(new OutboxTestEvent(7, "alice"));
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        var bus = new OutboxIntegrationEventBus(new FakeOutboxStore(), new OutboxOptions(), TimeProvider.System);

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
    public async Task DispatchAsync_UnknownEventType_IsNoOp()
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
        await dispatcher.DispatchAsync(EmptyProvider.Instance, message, TestContext.Current.CancellationToken);

        invoked.Should().BeFalse();
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
            new OutboxOptions());

    private static OutboxMessage CreateMessage(OutboxTestEvent @event, Guid correlationId) => new()
    {
        Id = Guid.NewGuid(),
        OccurredOnUtc = DateTimeOffset.UnixEpoch,
        EventType = typeof(OutboxTestEvent).FullName!,
        Payload = JsonSerializer.Serialize(@event, new OutboxOptions().SerializerOptions),
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

        await RunUntilSignaledAsync(store, recordingDispatcher: (_, _, _, _) => ValueTask.CompletedTask);

        store.Processed.Should().ContainSingle().Which.Should().Be(id);
        store.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedConsumer_MarksFailed()
    {
        var store = new FakeOutboxStore();
        store.Pending.Enqueue([Message(new OutboxTestEvent(1, "a"), out var id)]);

        await RunUntilSignaledAsync(store, recordingDispatcher: (_, _, _, _) => throw new InvalidOperationException("boom"));

        store.Failed.Should().ContainSingle().Which.Id.Should().Be(id);
        store.Processed.Should().BeEmpty();
    }

    private static async Task RunUntilSignaledAsync(FakeOutboxStore store, EventSubscriberInvokeDelegate recordingDispatcher)
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
            new OutboxOptions());

        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) },
            new FakeTimeProvider(),
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
            Payload = JsonSerializer.Serialize(@event, new OutboxOptions().SerializerOptions),
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

    private sealed class OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options) : DbContext(options);
}

internal sealed class FakeOutboxStore : IOutboxStore
{
    public List<OutboxMessage> Appended { get; } = [];
    public Queue<IReadOnlyList<OutboxMessage>> Pending { get; } = new();
    public List<Guid> Processed { get; } = [];
    public List<(Guid Id, string Error)> Failed { get; } = [];
    public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Append(OutboxMessage message) => Appended.Add(message);

    public ValueTask<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        Guid lockId,
        DateTimeOffset leaseUntil,
        int batchSize,
        CancellationToken ct) =>
        ValueTask.FromResult(Pending.Count > 0 ? Pending.Dequeue() : (IReadOnlyList<OutboxMessage>)[]);

    public ValueTask MarkProcessedAsync(Guid id, DateTimeOffset processedOnUtc, CancellationToken ct)
    {
        Processed.Add(id);
        Signal.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(Guid id, string error, CancellationToken ct)
    {
        Failed.Add((id, error));
        Signal.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PurgeProcessedAsync(DateTimeOffset olderThanUtc, CancellationToken ct) =>
        ValueTask.FromResult(0);
}

internal sealed class EmptyProvider : IServiceProvider
{
    public static readonly EmptyProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
