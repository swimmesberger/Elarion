using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
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
            Subscriber(typeof(SampleDomainEvent), order: 1, label: "second"),
            Subscriber(typeof(SampleDomainEvent), order: 0, label: "first"));

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
            Throwing(typeof(SampleDomainEvent), order: 0, message: "boom-1"),
            Subscriber(typeof(SampleDomainEvent), order: 1, label: "ran"),
            Throwing(typeof(SampleDomainEvent), order: 2, message: "boom-2"));

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
            Throwing(typeof(SampleDomainEvent), order: 0, message: "only"));

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
    public async Task RequestAsync_ReturnsResponderResult() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(Responder(typeof(SampleRequest), typeof(int)));

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var result = await bus.RequestAsync<SampleRequest, int>(new SampleRequest(21), cts.Token);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task RequestAsync_WithoutResponder_Throws() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider();

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var act = async () => await bus.RequestAsync<SampleRequest, int>(new SampleRequest(1), cts.Token);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RequestAsync_DispatchesToHandlerResponder_ReturnsTypedResult() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            services => services.AddScoped<IHandler<SampleRequest, Result<int>>, DoublingHandler>(),
            HandlerResponder());

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var result = await bus.RequestAsync<SampleRequest, int>(new SampleRequest(21), cts.Token);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task RequestAsync_HandlerResponderFailure_ReturnsFailureResult() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            services => services.AddScoped<IHandler<SampleRequest, Result<int>>, FailingResponder>(),
            HandlerResponder());

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        var result = await bus.RequestAsync<SampleRequest, int>(new SampleRequest(-1), cts.Token);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task IntegrationEvent_IsDeliveredOnlyAfterFlush() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Subscriber(typeof(SampleIntegrationEvent), order: 0, label: "integration"));

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
    public async Task IntegrationEvent_DiscardDropsBufferedEvents() {
        using var cts = new CancellationTokenSource(WaitTimeout);
        await using var provider = BuildProvider(
            Subscriber(typeof(SampleIntegrationEvent), order: 0, label: "integration"));

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

    [Fact]
    public void Registry_RejectsDuplicateResponder() {
        var provider = BuildProvider(
            Responder(typeof(SampleRequest), typeof(int)),
            Responder(typeof(SampleRequest), typeof(int)));

        using var scope = provider.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<IDomainEventBus>();

        act.Should().Throw<InvalidOperationException>();
        provider.Dispose();
    }

    private static ServiceProvider BuildProvider(params EventSubscriptionDescriptor[] descriptors) =>
        BuildProvider(static _ => { }, descriptors);

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection> configure,
        params EventSubscriptionDescriptor[] descriptors) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<EventRecorder>();
        services.AddSingleton<ScopeProbe>();
        services.AddScoped<ScopeMarker>();
        foreach (var descriptor in descriptors) {
            services.AddSingleton(descriptor);
        }

        configure(services);
        services.AddInMemoryEventBus();
        return services.BuildServiceProvider();
    }

    // Mirrors the descriptor emitted by EventConsumerRegistrationGenerator for a handler-form
    // consumer: resolve the IHandler<,> interface (decorated), invoke it, and surface a failed
    // Result as an EventConsumerFailedException.
    private static EventSubscriptionDescriptor HandlerSubscriber(Type eventType) =>
        new() {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(IHandler<SampleDomainEvent, Result<Unit>>),
            Order = 0,
            InvokeAsync = static async (sp, evt, _, ct) => {
                var handler = sp.GetRequiredService<IHandler<SampleDomainEvent, Result<Unit>>>();
                var result = await handler.HandleAsync((SampleDomainEvent)evt, ct).ConfigureAwait(false);
                if (!result.IsSuccess) {
                    throw new EventConsumerFailedException(result.Error);
                }
            }
        };

    private static EventSubscriptionDescriptor Subscriber(Type eventType, int order, string label) =>
        new() {
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

    private static EventSubscriptionDescriptor Throwing(Type eventType, int order, string message) =>
        new() {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(EventRecorder),
            Order = order,
            InvokeAsync = (_, _, _, _) => throw new InvalidOperationException(message)
        };

    private static EventSubscriptionDescriptor ScopeCapturing(Type eventType) =>
        new() {
            EventType = eventType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(ScopeProbe),
            Order = 0,
            InvokeAsync = (sp, _, _, _) => {
                sp.GetRequiredService<ScopeProbe>().Captured = sp.GetRequiredService<ScopeMarker>();
                return ValueTask.CompletedTask;
            }
        };

    // Mirrors the descriptor emitted by EventConsumerRegistrationGenerator for a handler-form
    // responder: resolve the decorated IHandler<,> interface and hand its typed Result<T> back to
    // the RequestAsync caller (no failure-to-exception conversion).
    private static EventSubscriptionDescriptor HandlerResponder() =>
        new() {
            EventType = typeof(SampleRequest),
            Plane = EventPlane.Domain,
            ServiceType = typeof(IHandler<SampleRequest, Result<int>>),
            ResponseType = typeof(int),
            InvokeRequestAsync = static async (sp, request, _, ct) => {
                var handler = sp.GetRequiredService<IHandler<SampleRequest, Result<int>>>();
                return (object)await handler.HandleAsync((SampleRequest)request, ct).ConfigureAwait(false);
            }
        };

    private static EventSubscriptionDescriptor Responder(Type requestType, Type responseType) =>
        new() {
            EventType = requestType,
            Plane = EventPlane.Domain,
            ServiceType = typeof(EventRecorder),
            ResponseType = responseType,
            InvokeRequestAsync = (_, req, _, _) => {
                var request = (SampleRequest)req;
                return ValueTask.FromResult<object>(Result<int>.Success(request.Value * 2));
            }
        };

    private static string Payload(object evt) => evt switch {
        SampleDomainEvent domain => domain.Value,
        SampleIntegrationEvent integration => integration.Value,
        _ => evt.ToString() ?? string.Empty
    };

    private sealed record SampleDomainEvent(string Value) : IDomainEvent;

    private sealed record SampleIntegrationEvent(string Value) : IIntegrationEvent;

    private sealed record SampleRequest(int Value) : IDomainEvent;

    // Implements the IHandler<T> sugar, so it is resolvable as IHandler<T, Result<Unit>> via the
    // default interface method that bridges Result -> Result<Unit>.
    private sealed class RecordingHandler(EventRecorder recorder) : IHandler<SampleDomainEvent> {
        public ValueTask<Result> HandleAsync(SampleDomainEvent request, CancellationToken ct) {
            recorder.Add($"handler:{request.Value}");
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class FailingHandler : IHandler<SampleDomainEvent> {
        public ValueTask<Result> HandleAsync(SampleDomainEvent request, CancellationToken ct) =>
            ValueTask.FromResult(Result.Failure(AppError.Conflict("nope")));
    }

    private sealed class DoublingHandler : IHandler<SampleRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(SampleRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(request.Value * 2));
    }

    private sealed class FailingResponder : IHandler<SampleRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(SampleRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Failure(AppError.Validation("bad")));
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
            for (var i = 0; i < count; i++) {
                await _signal.WaitAsync(ct);
            }
        }
    }
}
