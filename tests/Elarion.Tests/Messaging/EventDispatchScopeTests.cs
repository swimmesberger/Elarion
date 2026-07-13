using AwesomeAssertions;
using Elarion.Abstractions.Messaging;
using Elarion.Messaging.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Messaging;

/// <summary>
/// Behavioural tests for the in-memory integration-event buffer: savepoint awareness (C4), the
/// undelivered-on-scope-end warning (H3), idempotent registration (M5), the synchronous-flush drop
/// policy (M6), and the pump's continue-on-processing-failure guarantee (M7).
/// </summary>
public sealed class EventDispatchScopeTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RollbackToSavepoint_DropsOnlyEventsBufferedAfterTheSavepoint() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        await using var provider = BuildProvider(recorder, Subscriber("consumed"));

        var pump = provider.GetServices<IHostedService>().OfType<EventDispatchPump>().Single();
        await pump.StartAsync(cts.Token);

        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();

            await bus.PublishAsync(new SampleIntegrationEvent("before"), cts.Token);
            dispatch.PushSavepoint();
            await bus.PublishAsync(new SampleIntegrationEvent("after"), cts.Token);

            // The idempotency decorator rolls a failed command back to its savepoint yet still commits the
            // outer transaction to persist the failure record: only pre-savepoint events must survive.
            dispatch.RollbackToSavepoint();
            await dispatch.FlushAsync(cts.Token);
        }

        await recorder.WaitForAsync(1, cts.Token);
        recorder.Items.Should().Equal("consumed:before");

        await pump.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ReleaseSavepoint_KeepsPostSavepointEventsForDelivery() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        await using var provider = BuildProvider(recorder, Subscriber("consumed"));

        var pump = provider.GetServices<IHostedService>().OfType<EventDispatchPump>().Single();
        await pump.StartAsync(cts.Token);

        using (var scope = provider.CreateScope()) {
            var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();
            var dispatch = scope.ServiceProvider.GetRequiredService<EventDispatchScope>();

            await bus.PublishAsync(new SampleIntegrationEvent("before"), cts.Token);
            dispatch.PushSavepoint();
            await bus.PublishAsync(new SampleIntegrationEvent("after"), cts.Token);
            dispatch.ReleaseSavepoint();
            await dispatch.FlushAsync(cts.Token);
        }

        await recorder.WaitForAsync(2, cts.Token);
        recorder.Items.Should().BeEquivalentTo("consumed:before", "consumed:after");

        await pump.StopAsync(cts.Token);
    }

    [Fact]
    public void Dispose_WithUnflushedBuffer_LogsWarningNamingEventsAndCount() {
        using var provider = BuildProvider(new EventRecorder());
        var pump = provider.GetRequiredService<EventDispatchPump>();
        var logger = new RecordingLogger<EventDispatchScope>();
        var scope = new EventDispatchScope(pump, logger);
        scope.Add(Envelope("x"));

        scope.Dispose();

        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(nameof(SampleIntegrationEvent));
        warning.Message.Should().Contain("dropped without delivery");
    }

    [Fact]
    public async Task Dispose_AfterFlush_DoesNotWarn() {
        using var provider = BuildProvider(new EventRecorder());
        var pump = provider.GetRequiredService<EventDispatchPump>();
        var logger = new RecordingLogger<EventDispatchScope>();
        var scope = new EventDispatchScope(pump, logger);
        scope.Add(Envelope("x"));

        await scope.FlushAsync(TestContext.Current.CancellationToken);
        scope.Dispose();

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Dispose_WithEventsBufferedAfterAFlush_StillWarns() {
        using var provider = BuildProvider(new EventRecorder());
        var pump = provider.GetRequiredService<EventDispatchPump>();
        var logger = new RecordingLogger<EventDispatchScope>();
        var scope = new EventDispatchScope(pump, logger);

        // A save flushes the buffer (autocommit), then another event is published with no further save: nothing
        // flushes it again, so it is dropped — an earlier flush must not suppress the warning.
        scope.Add(Envelope("committed"));
        await scope.FlushAsync(TestContext.Current.CancellationToken);
        scope.Add(Envelope("dropped"));

        scope.Dispose();

        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(nameof(SampleIntegrationEvent));
        warning.Message.Should().Contain("dropped without delivery");
    }

    [Fact]
    public void AddElarionInMemoryIntegrationEventBus_CalledTwice_RegistersSinglePumpHostedService() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElarionInMemoryIntegrationEventBus();
        services.AddElarionInMemoryIntegrationEventBus();

        using var provider = services.BuildServiceProvider();
        var pumps = provider.GetServices<IHostedService>().OfType<EventDispatchPump>().ToList();
        pumps.Should().HaveCount(1);
    }

    [Fact]
    public void AddElarionInMemoryIntegrationEventBus_CalledTwice_RegistersEachInterceptorOnce() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddElarionInMemoryIntegrationEventBus();
        services.AddElarionInMemoryIntegrationEventBus();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var interceptors = scope.ServiceProvider
            .GetServices<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>()
            .ToList();

        interceptors.Should().ContainSingle(i => i is EventDispatchSaveChangesInterceptor);
        interceptors.Should().ContainSingle(i => i is EventDispatchTransactionInterceptor);
    }

    [Fact]
    public void FlushSynchronously_WhenChannelIsFull_DropsWithErrorLogInsteadOfBlocking() {
        var logger = new RecordingLogger<EventDispatchPump>();
        var options = new EventBusOptions { DeliveryChannelCapacity = 1 };
        using var provider = BuildProvider(new EventRecorder(), options: options, pumpLogger: logger);
        var pump = provider.GetRequiredService<EventDispatchPump>();

        var scope = new EventDispatchScope(pump, new RecordingLogger<EventDispatchScope>());
        // Two events into a capacity-1 channel with no reader running: the first fits, the second overflows.
        scope.Add(Envelope("a"));
        scope.Add(Envelope("b"));

        // Must return without blocking the (synchronous) commit thread.
        scope.FlushSynchronously();

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Message.Contains("Dropped integration event"));
    }

    [Fact]
    public async Task Pump_WhenScopeFactoryThrows_LogsAndKeepsDeliveringLaterEvents() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        var recorder = new EventRecorder();
        var pumpLogger = new RecordingLogger<EventDispatchPump>();
        var factory = new TogglingScopeFactory(recorder);
        // Drive the pump directly with a scope factory whose CreateScope can be made to throw — a failure the
        // per-consumer try/catch does NOT cover (it happens before any consumer runs). The loop must log and
        // survive to deliver later events instead of exiting for the process lifetime.
        var registry = new Elarion.Messaging.EventSubscriptionRegistry([SubscriberDescriptor("consumed")]);
        var pump = new EventDispatchPump(registry, factory, new EventBusOptions(), pumpLogger);

        await pump.StartAsync(cts.Token);

        factory.Fail = true;
        await pump.EnqueueAsync(Envelope("first"), cts.Token);
        await WaitUntilAsync(() => pumpLogger.Entries.Any(e => e.Level == LogLevel.Error), cts.Token);

        factory.Fail = false;
        await pump.EnqueueAsync(Envelope("second"), cts.Token);

        await recorder.WaitForAsync(1, cts.Token);
        recorder.Items.Should().Equal("consumed:second");
        pumpLogger.Entries.Should().Contain(e => e.Level == LogLevel.Error);

        await pump.StopAsync(cts.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct) {
        while (!condition()) {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private static EventEnvelope Envelope(string value) {
        var evt = new SampleIntegrationEvent(value);
        return new EventEnvelope(evt, typeof(SampleIntegrationEvent), new TestEventContext(evt));
    }

    private static ServiceProvider BuildProvider(
        EventRecorder recorder,
        EventSubscriptionDescriptor? descriptor = null,
        EventBusOptions? options = null,
        ILogger<EventDispatchPump>? pumpLogger = null,
        Action<IServiceCollection>? configure = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        if (descriptor is not null) {
            services.AddSingleton(descriptor);
        }

        services.AddElarionInMemoryIntegrationEventBus(options);
        if (pumpLogger is not null) {
            services.AddSingleton(pumpLogger);
        }

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static EventSubscriptionDescriptor Subscriber(string label) =>
        new() {
            EventType = typeof(SampleIntegrationEvent),
            Plane = EventPlane.Integration,
            ServiceType = typeof(EventRecorder),
            Order = 0,
            InvokeAsync = (sp, evt, _, _) => {
                sp.GetRequiredService<EventRecorder>().Add($"{label}:{((SampleIntegrationEvent)evt).Value}");
                return ValueTask.CompletedTask;
            }
        };

    private sealed record SampleIntegrationEvent(string Value) : IIntegrationEvent;

    private sealed class TestEventContext(SampleIntegrationEvent message) : IEventContext {
        public Guid CorrelationId { get; } = Guid.NewGuid();
        public Guid? MessageId { get; } = Guid.NewGuid();
        public EventPlane Plane => EventPlane.Integration;
        public object Message => message;
    }

    private static EventSubscriptionDescriptor SubscriberDescriptor(string label) =>
        new() {
            EventType = typeof(SampleIntegrationEvent),
            Plane = EventPlane.Integration,
            ServiceType = typeof(EventRecorder),
            Order = 0,
            InvokeAsync = (sp, evt, _, _) => {
                var recorder = (EventRecorder)sp.GetService(typeof(EventRecorder))!;
                recorder.Add($"{label}:{((SampleIntegrationEvent)evt).Value}");
                return ValueTask.CompletedTask;
            }
        };

    private sealed class TogglingScopeFactory(EventRecorder recorder) : IServiceScopeFactory {
        public volatile bool Fail;

        public IServiceScope CreateScope() {
            if (Fail) {
                throw new InvalidOperationException("scope factory boom");
            }

            return new Scope(recorder);
        }

        private sealed class Scope(EventRecorder recorder) : IServiceScope, IServiceProvider {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(EventRecorder) ? recorder : null;
            public void Dispose() { }
        }
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
            for (var i = 0; i < count; i++) {
                await _signal.WaitAsync(ct);
            }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T> {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries {
            get {
                lock (_entries) {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) {
            lock (_entries) {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
