using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Results;
using Elarion.Messaging;
using Elarion.Messaging.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Messaging;

public sealed class InMemoryEventBusTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PublishAsync_DomainEvent_InvokesSubscribersInOrder() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Subscriber(typeof(SampleDomainEvent), 1, "second"),
            Subscriber(typeof(SampleDomainEvent), 0, "first"));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        var recorder = provider.GetRequiredService<EventRecorder>();
        recorder.Items.Should().Equal("first:payload", "second:payload");
    }

    [Fact]
    public async Task PublishAsync_DomainEvent_RunsEverySubscriberAndAggregatesFailures() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Throwing(typeof(SampleDomainEvent), 0, "boom-1"),
            Subscriber(typeof(SampleDomainEvent), 1, "ran"),
            Throwing(typeof(SampleDomainEvent), 2, "boom-2"));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var act = async () => await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        var aggregate = (await act.Should().ThrowAsync<AggregateException>()).Which;
        aggregate.InnerExceptions.Select(e => e.Message).Should().BeEquivalentTo("boom-1", "boom-2");
        provider.GetRequiredService<EventRecorder>().Items.Should().Equal("ran:payload");
    }

    [Fact]
    public async Task PublishAsync_DomainEvent_SingleFailureIsNotWrapped() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Throwing(typeof(SampleDomainEvent), 0, "only"));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var act = async () => await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("only");
    }

    [Fact]
    public async Task PublishAsync_DomainEvent_SharesCallerScope() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(ScopeCapturing(typeof(SampleDomainEvent)));

        using var scope = provider.CreateScope();
        var expected = scope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        provider.GetRequiredService<ScopeProbe>().Captured.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task IntegrationEvent_IsDeliveredOnlyAfterFlush() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Subscriber(typeof(SampleIntegrationEvent), 0, "integration"));

        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        var recorder = provider.GetRequiredService<EventRecorder>();
        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();

            await bus.PublishAsync(new SampleIntegrationEvent("y"), cts.Token);
            recorder.Items.Should().BeEmpty();

            await dispatch.FlushAsync(cts.Token);
        }

        await recorder.WaitForAsync(1, cts.Token);
        recorder.Items.Should().Equal("integration:y");

        await pump.StopAsync(cts.Token);
    }

    [Fact]
    public async Task IntegrationEvent_CarriesAMessageId_AndSeedsItAsTheScopeIdempotencyKey() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        Guid? contextMessageId = null;
        string? seededKey = null;
        var probe = new EventSubscriptionDescriptor {
            EventType = typeof(SampleIntegrationEvent),
            Plane = EventPlane.Integration,
            ServiceType = typeof(EventRecorder),
            Order = 0,
            InvokeAsync = (sp, _, context, _) => {
                contextMessageId = context.MessageId;
                sp.GetRequiredService<Elarion.Abstractions.Idempotency.IIdempotencyKeyAccessor>()
                    .TryGetKey(out seededKey);
                sp.GetRequiredService<EventRecorder>().Add("probed");
                return ValueTask.CompletedTask;
            }
        };
        await using var provider = BuildProvider(probe);

        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();
            await bus.PublishAsync(new SampleIntegrationEvent("y"), cts.Token);
            await dispatch.FlushAsync(cts.Token);
        }

        await provider.GetRequiredService<EventRecorder>().WaitForAsync(1, cts.Token);

        // The in-memory tier assigns a per-publish message id and seeds it into the delivery scope, so the
        // consumer-visible contract (IEventContext.MessageId, the inbox key) matches the outbox tier.
        contextMessageId.Should().NotBeNull();
        seededKey.Should().Be(contextMessageId!.Value.ToString("N"));

        await pump.StopAsync(cts.Token);
    }

    [Fact]
    public async Task DomainEvent_HasNoMessageId() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        Guid? contextMessageId = Guid.Empty;
        var probe = new EventSubscriptionDescriptor {
            EventType = typeof(SampleDomainEvent),
            Plane = EventPlane.Domain,
            ServiceType = typeof(EventRecorder),
            Order = 0,
            InvokeAsync = (_, _, context, _) => {
                contextMessageId = context.MessageId;
                return ValueTask.CompletedTask;
            }
        };
        await using var provider = BuildProvider(probe);

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDomainEventBus>()
            .PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        // Inline dispatch in the publisher's transaction: there is no message, so there is no message id.
        contextMessageId.Should().BeNull();
    }

    [Fact]
    public async Task IntegrationEvent_DiscardDropsBufferedEvents() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Subscriber(typeof(SampleIntegrationEvent), 0, "integration"));

        var pump = provider.GetServices<IHostedService>().Single();
        await pump.StartAsync(cts.Token);

        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();

            await bus.PublishAsync(new SampleIntegrationEvent("y"), cts.Token);
            dispatch.Discard();
            await dispatch.FlushAsync(cts.Token);
        }

        await Task.Delay(100, cts.Token);
        provider.GetRequiredService<EventRecorder>().Items.Should().BeEmpty();

        await pump.StopAsync(cts.Token);
    }

    [Fact]
    public async Task PublishAsync_DomainEvent_DispatchesToHandlerResolvedFromDi() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            services => services.AddScoped<IHandler<SampleDomainEvent, Result<Unit>>, RecordingHandler>(),
            HandlerSubscriber(typeof(SampleDomainEvent)));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        provider.GetRequiredService<EventRecorder>().Items.Should().Equal("handler:payload");
    }

    [Fact]
    public async Task PublishAsync_DomainEvent_HandlerFailure_ThrowsEventConsumerFailed() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            services => services.AddScoped<IHandler<SampleDomainEvent, Result<Unit>>, FailingHandler>(),
            HandlerSubscriber(typeof(SampleDomainEvent)));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var act = async () => await bus.PublishAsync(new SampleDomainEvent("payload"), cts.Token);

        var ex = (await act.Should().ThrowAsync<EventConsumerFailedException>()).Which;
        ex.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    private static ServiceProvider BuildProvider(params EventSubscriptionDescriptor[] descriptors) {
        return BuildProvider(static _ => { }, descriptors);
    }

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection> configure,
        params EventSubscriptionDescriptor[] descriptors) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<EventRecorder>();
        services.AddSingleton<ScopeProbe>();
        services.AddScoped<ScopeMarker>();
        foreach (var descriptor in descriptors) services.AddSingleton(descriptor);

        configure(services);
        // These tests drive FlushAsync/Discard by hand (no database), so they use the low-level building blocks
        // rather than the TContext overload that auto-attaches the commit-gating interceptors.
        services.AddElarionDomainEventBus();
        services.AddElarionInMemoryIntegrationEventBus();
        return services.BuildServiceProvider();
    }

    // Mirrors the descriptor emitted by EventConsumerRegistrationGenerator for a handler-form
    // consumer: resolve the IHandler<,> interface (decorated), invoke it, and surface a failed
    // Result as an EventConsumerFailedException.
    private static EventSubscriptionDescriptor HandlerSubscriber(Type eventType) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(IHandler<SampleDomainEvent, Result<Unit>>),
            Order = 0,
            InvokeAsync = static async (sp, evt, _, ct) => {
                var handler = sp.GetRequiredService<IHandler<SampleDomainEvent, Result<Unit>>>();
                var result = await handler.HandleAsync((SampleDomainEvent)evt, ct).ConfigureAwait(false);
                if (!result.IsSuccess) throw new EventConsumerFailedException(result.Error);
            }
        };
    }

    private static EventSubscriptionDescriptor Subscriber(Type eventType, int order, string label) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = eventType == typeof(SampleIntegrationEvent) ? EventPlane.Integration : EventPlane.Domain,
            ServiceType = typeof(EventRecorder),
            Order = order,
            InvokeAsync = (sp, evt, _, _) => {
                var recorder = sp.GetRequiredService<EventRecorder>();
                recorder.Add($"{label}:{Payload(evt)}");
                return ValueTask.CompletedTask;
            }
        };
    }

    private static EventSubscriptionDescriptor Throwing(Type eventType, int order, string message) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(EventRecorder),
            Order = order,
            InvokeAsync = (_, _, _, _) => throw new InvalidOperationException(message)
        };
    }

    private static EventSubscriptionDescriptor ScopeCapturing(Type eventType) {
        return new EventSubscriptionDescriptor {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(ScopeProbe),
            Order = 0,
            InvokeAsync = (sp, _, _, _) => {
                sp.GetRequiredService<ScopeProbe>().Captured = sp.GetRequiredService<ScopeMarker>();
                return ValueTask.CompletedTask;
            }
        };
    }

    private static string Payload(object evt) {
        return evt switch {
            SampleDomainEvent domain => domain.Value,
            SampleIntegrationEvent integration => integration.Value,
            _ => evt.ToString() ?? string.Empty
        };
    }

    private sealed record SampleDomainEvent(string Value) : IDomainEvent;

    private sealed record SampleIntegrationEvent(string Value) : IIntegrationEvent;

    // Implements the IHandler<T> sugar, so it is resolvable as IHandler<T, Result<Unit>> via the
    // default interface method that bridges Result -> Result<Unit>.
    private sealed class RecordingHandler(EventRecorder recorder) : IHandler<SampleDomainEvent> {
        public ValueTask<Result> HandleAsync(SampleDomainEvent request, CancellationToken ct) {
            recorder.Add($"handler:{request.Value}");
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class FailingHandler : IHandler<SampleDomainEvent> {
        public ValueTask<Result> HandleAsync(SampleDomainEvent request, CancellationToken ct) {
            return ValueTask.FromResult(Result.Failure(AppError.Conflict("nope")));
        }
    }

    private sealed class ScopeMarker;

    private sealed class ScopeProbe {
        public ScopeMarker? Captured { get; set; }
    }

    private sealed class EventRecorder {
        private readonly object _gate = new();
        private readonly List<string> _items = [];
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<string> Items {
            get {
                lock (_gate) {
                    return _items.ToArray();
                }
            }
        }

        public void Add(string item) {
            lock (_gate) {
                _items.Add(item);
            }

            _signal.Release();
        }

        public async Task WaitForAsync(int count, CancellationToken ct) {
            for (var i = 0; i < count; i++) await _signal.WaitAsync(ct);
        }
    }
}
