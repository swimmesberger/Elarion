using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging;
using Elarion.Messaging.InMemory;
using Elarion.Messaging.Outbox;
using Elarion.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// Telemetry regression tests for the event/messaging subsystem: publish/consume spans, bounded metrics,
/// and the publish-time trace context surviving the commit boundary into after-commit consumers.
/// </summary>
public sealed class EventTelemetryTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private sealed record TelemetryDomainEvent(string Value) : IDomainEvent;

    private sealed record TelemetryIntegrationEvent(string Value) : IIntegrationEvent;

    [Fact]
    public async Task DomainPublish_EmitsPublishSpanAndConsumerMetrics() {
        using var activities = new ActivityCollector(EventTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(EventTelemetry.MeterName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(Subscriber<TelemetryDomainEvent>(EventPlane.Domain));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();
        await bus.PublishAsync(new TelemetryDomainEvent("x"), cts.Token);

        var publish = activities.Activities.Should()
            .ContainSingle(activity => activity.OperationName == "publish TelemetryDomainEvent").Subject;
        publish.GetTag("messaging.event.plane").Should().Be("domain");
        publish.GetTag("messaging.subscriber.count").Should().Be(1);

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.event.publish.count" &&
            m.HasTag("messaging.event.type", "TelemetryDomainEvent") &&
            m.HasTag("messaging.event.plane", "domain"));
        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.consumer.invocation.count" &&
            m.HasTag("messaging.event.type", "TelemetryDomainEvent") &&
            m.HasTag("messaging.consumer.outcome", "ok"));
    }

    [Fact]
    public async Task DomainPublish_ConsumerFailure_MarksSpanErrored() {
        using var activities = new ActivityCollector(EventTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(EventTelemetry.MeterName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(Throwing<TelemetryDomainEvent>(EventPlane.Domain));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();
        var act = async () => await bus.PublishAsync(new TelemetryDomainEvent("x"), cts.Token);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var publish = activities.Activities.Should()
            .ContainSingle(activity => activity.OperationName == "publish TelemetryDomainEvent").Subject;
        publish.Status.Should().Be(ActivityStatusCode.Error);
        publish.Events.Should().Contain(e => e.Name == "exception");

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.consumer.invocation.count" &&
            m.HasTag("messaging.event.type", "TelemetryDomainEvent") &&
            m.HasTag("messaging.consumer.outcome", "exception"));
    }

    [Fact]
    public async Task IntegrationEvent_ConsumeSpan_IsParentedToPublishTrace() {
        using var activities = new ActivityCollector(EventTelemetry.ActivitySourceName);
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(Subscriber<TelemetryIntegrationEvent>(EventPlane.Integration));

        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        using var publisherActivity = new Activity("command").Start();
        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();
            await bus.PublishAsync(new TelemetryIntegrationEvent("y"), cts.Token);
            await dispatch.FlushAsync(cts.Token);
        }

        await provider.GetRequiredService<Recorder>().WaitForAsync(1, cts.Token);
        await pump.StopAsync(cts.Token);

        var consume = activities.Activities.Should()
            .ContainSingle(activity => activity.OperationName == "consume TelemetryIntegrationEvent").Subject;
        consume.TraceId.Should().Be(publisherActivity.TraceId);
        consume.ParentSpanId.Should().Be(publisherActivity.SpanId);
        consume.GetTag("messaging.event.plane").Should().Be("integration");
        consume.GetTag("messaging.correlation_id").Should().NotBeNull();
    }

    [Fact]
    public async Task OutboxPublish_PersistsTraceParent() {
        var store = new FakeOutboxStore();
        var bus = new OutboxIntegrationEventBus(
            store,
            new OutboxConsumerCatalog([Subscriber<OutboxTestEvent>(EventPlane.Integration)]),
            EmptyProvider.Instance,
            new OutboxOptions(),
            OutboxTestJson.Instance,
            TimeProvider.System);

        using var publisherActivity = new Activity("command").Start();
        await bus.PublishAsync(new OutboxTestEvent(1, "a"), TestContext.Current.CancellationToken);

        store.Appended.Should().ContainSingle().Which.TraceParent.Should().Be(publisherActivity.Id);
    }

    [Fact]
    public async Task OutboxDelivery_ConsumeSpan_IsParentedToPersistedTraceAndRecordsMetrics() {
        using var activities = new ActivityCollector(EventTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(EventTelemetry.MeterName);

        using var publisherActivity = new Activity("command").Start();
        var correlationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var message = new OutboxMessage {
            Id = messageId,
            MessageId = messageId,
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = typeof(OutboxTestEvent).FullName!,
            Payload = JsonSerializer.Serialize(new OutboxTestEvent(1, "a"), OutboxTestJson.Instance.Options),
            CorrelationId = correlationId,
            TraceParent = publisherActivity.Id,
        };
        publisherActivity.Stop();

        var store = new FakeOutboxStore();
        store.Pending.Enqueue([message]);
        await RunDeliveryUntilSignaledAsync(store, (_, _, _, _) => ValueTask.CompletedTask);

        // The collector listens globally on the shared source; scope to this test's correlation id so
        // parallel test classes delivering the same event type cannot leak a second consume activity in.
        var consume = activities.Activities.Should()
            .ContainSingle(activity => activity.OperationName == $"consume {typeof(OutboxTestEvent).FullName}" &&
                                       Equals(activity.GetTag("messaging.correlation_id"), correlationId)).Subject;
        consume.TraceId.Should().Be(publisherActivity.TraceId);
        consume.ParentSpanId.Should().Be(publisherActivity.SpanId);
        consume.GetTag("messaging.outbox.attempt").Should().Be(1);

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.delivery.count" &&
            m.HasTag("messaging.event.type", typeof(OutboxTestEvent).FullName) &&
            m.HasTag("messaging.delivery.outcome", "delivered"));
        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.consumer.invocation.count" &&
            m.HasTag("messaging.event.type", nameof(OutboxTestEvent)) &&
            m.HasTag("messaging.consumer.outcome", "ok"));
    }

    [Fact]
    public async Task OutboxDelivery_FailedConsumer_RecordsFailedOutcome() {
        using var activities = new ActivityCollector(EventTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(EventTelemetry.MeterName);

        var correlationId = Guid.NewGuid();
        var store = new FakeOutboxStore();
        var messageId = Guid.NewGuid();
        store.Pending.Enqueue([new OutboxMessage {
            Id = messageId,
            MessageId = messageId,
            OccurredOnUtc = DateTimeOffset.UnixEpoch,
            EventType = typeof(OutboxTestEvent).FullName!,
            Payload = JsonSerializer.Serialize(new OutboxTestEvent(1, "a"), OutboxTestJson.Instance.Options),
            CorrelationId = correlationId,
        }]);
        await RunDeliveryUntilSignaledAsync(store, (_, _, _, _) => throw new InvalidOperationException("boom"));

        // Scoped to this test's correlation id — see OutboxDelivery_ConsumeSpan for why.
        var consume = activities.Activities.Should()
            .ContainSingle(activity => activity.OperationName == $"consume {typeof(OutboxTestEvent).FullName}" &&
                                       Equals(activity.GetTag("messaging.correlation_id"), correlationId)).Subject;
        consume.Status.Should().Be(ActivityStatusCode.Error);

        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "messaging.delivery.count" &&
            m.HasTag("messaging.event.type", typeof(OutboxTestEvent).FullName) &&
            m.HasTag("messaging.delivery.outcome", "failed"));
    }

    private static async Task RunDeliveryUntilSignaledAsync(FakeOutboxStore store, EventSubscriberInvokeDelegate consumer) {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        await using var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxEventDispatcher(
            new OutboxConsumerCatalog([
                new EventSubscriptionDescriptor {
                    ConsumerId = "telemetry-consumer",
                    EventType = typeof(OutboxTestEvent),
                    Plane = EventPlane.Integration,
                    ServiceType = typeof(object),
                    InvokeAsync = consumer
                }
            ]),
            new OutboxOptions(),
            OutboxTestJson.Instance,
            NullLogger<OutboxEventDispatcher>.Instance);

        var service = new OutboxDeliveryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(20) },
            new FakeTimeProvider(),
            NullLogger<OutboxDeliveryService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.Signal.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildProvider(params EventSubscriptionDescriptor[] descriptors) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Recorder>();
        foreach (var descriptor in descriptors) {
            services.AddSingleton(descriptor);
        }

        services.AddElarionDomainEventBus();
        services.AddElarionInMemoryIntegrationEventBus();
        return services.BuildServiceProvider();
    }

    private static EventSubscriptionDescriptor Subscriber<TEvent>(EventPlane plane) =>
        new() {
            ConsumerId = $"subscriber:{typeof(TEvent).FullName}:{plane}",
            EventType = typeof(TEvent),
            Plane = plane,
            ServiceType = typeof(Recorder),
            InvokeAsync = (sp, _, _, _) => {
                sp.GetRequiredService<Recorder>().Add();
                return ValueTask.CompletedTask;
            }
        };

    private static EventSubscriptionDescriptor Throwing<TEvent>(EventPlane plane) =>
        new() {
            ConsumerId = $"throwing:{typeof(TEvent).FullName}:{plane}",
            EventType = typeof(TEvent),
            Plane = plane,
            ServiceType = typeof(Recorder),
            InvokeAsync = (_, _, _, _) => throw new InvalidOperationException("boom")
        };

    private sealed class Recorder {
        private readonly SemaphoreSlim _signal = new(0);

        public void Add() => _signal.Release();

        public async Task WaitForAsync(int count, CancellationToken ct) {
            for (var i = 0; i < count; i++) {
                await _signal.WaitAsync(ct);
            }
        }
    }
}
