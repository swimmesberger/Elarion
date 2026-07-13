using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Serialization;
using Elarion.ClientEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.ClientEvents;

/// <summary>
/// Covers the producer-side subscription lifecycle: the <see cref="IClientEventInterest"/> pull check, the
/// per-subscriber greeting sink (<see cref="IClientEventSubscriptionObserver.OnSubscribedAsync"/> targets
/// only the new subscriber), and the debounced interest transitions (active once per epoch; inactive only
/// after the last subscriber leaves and the linger elapses — a resubscribe within it neither bounces the
/// producer nor re-fires active).
/// </summary>
public sealed partial class ClientEventLifecycleTests {
    private sealed record QuoteChanged : IClientEvent {
        public required string Symbol { get; init; }
    }

    private sealed record OrderChanged : IClientEvent {
        public required Guid OrderId { get; init; }
    }

    [JsonSerializable(typeof(QuoteChanged))]
    [JsonSerializable(typeof(OrderChanged))]
    private sealed partial class LifecycleTestContext : JsonSerializerContext;

    /// <summary>Observers are instantiated per callback, so recording goes through this shared singleton.</summary>
    private sealed class Recorder {
        public ConcurrentQueue<(ClientEventSubscription Subscription, bool Active)> InterestChanges { get; } = new();

        public ConcurrentQueue<ClientEventSubscription> Greetings { get; } = new();

        private TaskCompletionSource _signal = NewSignal();

        public Task WaitForNextAsync(CancellationToken ct) => _signal.Task.WaitAsync(ct);

        public void Pulse() {
            var previous = Interlocked.Exchange(ref _signal, NewSignal());
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GreetingObserver(Recorder recorder) : IClientEventSubscriptionObserver {
        public async ValueTask OnSubscribedAsync(
            ClientEventSubscription subscription, IClientEventSubscriberSink sink, CancellationToken ct) {
            await sink.PublishAsync(new QuoteChanged { Symbol = subscription.Scope.Value ?? "global" }, ct);
            recorder.Greetings.Enqueue(subscription);
            recorder.Pulse();
        }

        public ValueTask OnInterestChangedAsync(
            ClientEventSubscription subscription, bool active, CancellationToken ct) {
            recorder.InterestChanges.Enqueue((subscription, active));
            recorder.Pulse();
            return default;
        }
    }

    /// <summary>Lets a test hold the observer's inactive callback open to race it against a resubscribe.</summary>
    private sealed class InactiveGate {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedObserver(Recorder recorder, InactiveGate gate) : IClientEventSubscriptionObserver {
        public ValueTask OnSubscribedAsync(
            ClientEventSubscription subscription, IClientEventSubscriberSink sink, CancellationToken ct) {
            recorder.Greetings.Enqueue(subscription);
            recorder.Pulse();
            return default;
        }

        public async ValueTask OnInterestChangedAsync(
            ClientEventSubscription subscription, bool active, CancellationToken ct) {
            if (!active) {
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            }
            recorder.InterestChanges.Enqueue((subscription, active));
            recorder.Pulse();
        }
    }

    private static ServiceProvider BuildGatedProvider(FakeTimeProvider timeProvider) {
        var services = new ServiceCollection();
        services.AddSingleton(new Recorder());
        services.AddSingleton(new InactiveGate());
        services.AddSingleton<TimeProvider>(timeProvider);
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(LifecycleTestContext.Default));
        services.AddElarionClientEvents(events => events
            .AddTopic<QuoteChanged>("market.quoteChanged", t => t
                .AllowAnyResource()
                .ObserveSubscriptions<GatedObserver>()
                .WithInterestLinger(TimeSpan.FromSeconds(5))));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddSingleton(new Recorder());
        if (timeProvider is not null) {
            services.AddSingleton<TimeProvider>(timeProvider);
        }
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(LifecycleTestContext.Default));
        services.AddElarionClientEvents(events => events
            .AddTopic<QuoteChanged>("market.quoteChanged", t => t
                .AllowAnyResource()
                .ObserveSubscriptions<GreetingObserver>()
                .WithInterestLinger(TimeSpan.FromSeconds(5)))
            .AddTopic<OrderChanged>("orders.orderChanged"));
        return services.BuildServiceProvider();
    }

    private static ClientEventSubscription Sub(string resource) =>
        new() { Topic = "market.quoteChanged", Scope = ClientEventScope.Resource(resource) };

    private static async Task WaitUntilAsync(Recorder recorder, Func<bool> condition, CancellationToken ct) {
        while (!condition()) {
            await recorder.WaitForNextAsync(ct);
        }
    }

    [Fact]
    public void HasSubscribers_TracksExactTopicAndScope_AndClearsOnDispose() {
        using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var interest = provider.GetRequiredService<IClientEventInterest>();

        interest.HasSubscribers("market.quoteChanged", ClientEventScope.Resource("ELN")).Should().BeFalse();
        var handle = source.Subscribe([Sub("ELN")]);
        interest.HasSubscribers("market.quoteChanged", ClientEventScope.Resource("ELN")).Should().BeTrue();
        interest.HasSubscribers("market.quoteChanged", ClientEventScope.Resource("OTHER")).Should().BeFalse();
        interest.HasSubscribers("market.quoteChanged", ClientEventScope.Global).Should().BeFalse();

        handle.Dispose();
        // The pull check is immediate — only the observer transition is debounced.
        interest.HasSubscribers("market.quoteChanged", ClientEventScope.Resource("ELN")).Should().BeFalse();
    }

    [Fact]
    public async Task Observer_GreetsExactlyTheNewSubscriber() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();

        using var first = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
        (await first.Events.ReadAsync(ct)).Payload.Should().Contain("ELN");

        using var second = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 2, ct);

        // The second subscriber gets its greeting; the first sees nothing new.
        (await second.Events.ReadAsync(ct)).Payload.Should().Contain("ELN");
        first.Events.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Observer_SeesActiveBeforeTheGreeting_OncePerEpoch() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();

        using var first = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
        using var second = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 2, ct);

        // One active transition for two subscribers, observed before the first greeting.
        recorder.InterestChanges.Should().ContainSingle().Which.Active.Should().BeTrue();
        recorder.Greetings.Should().HaveCount(2);
    }

    [Fact]
    public async Task Observer_ReportsInactive_OnlyAfterTheLingerElapses() {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = BuildProvider(time);
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();

        var handle = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
        handle.Dispose();

        time.Advance(TimeSpan.FromSeconds(4.9));
        recorder.InterestChanges.Should().ContainSingle("the departure is still lingering");

        time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitUntilAsync(recorder, () => recorder.InterestChanges.Count == 2, ct);
        recorder.InterestChanges.Last().Active.Should().BeFalse();
    }

    [Fact]
    public async Task Resubscribe_WithinTheLinger_NeitherBouncesNorRefiresActive() {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = BuildProvider(time);
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();

        var handle = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
        handle.Dispose();

        // The reload: back within the linger.
        time.Advance(TimeSpan.FromSeconds(2));
        using var reconnected = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 2, ct);

        time.Advance(TimeSpan.FromSeconds(30));
        recorder.InterestChanges.Should().ContainSingle("no inactive fired, and active fired only once")
            .Which.Active.Should().BeTrue();
    }

    [Fact]
    public async Task LingerElapseRacingResubscribe_ObserverEndsActive_InTransitionOrder() {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var provider = BuildGatedProvider(time);
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();
        var gate = provider.GetRequiredService<InactiveGate>();

        var handle = source.Subscribe([Sub("ELN")]);
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
        handle.Dispose();

        // The linger elapses and the inactive callback starts — hold it open at the gate.
        time.Advance(TimeSpan.FromSeconds(5.1));
        await gate.Entered.Task.WaitAsync(ct);

        // A resubscribe races the still-running inactive: its active/greeting must queue BEHIND it, so the
        // observer's last observed transition matches the actual interest instead of a torn-down producer.
        using var reconnected = source.Subscribe([Sub("ELN")]);

        gate.Release.TrySetResult();
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 2, ct);

        recorder.InterestChanges.Select(change => change.Active).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task SinkPublish_AfterSubscriberDisconnected_IsANoOp() {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProvider();
        var source = provider.GetRequiredService<IClientEventSubscriptionSource>();
        var recorder = provider.GetRequiredService<Recorder>();

        // Dispose immediately: the greeting may land after the channel completed — it must not throw
        // (the observer's exception would be recorded as a missing greeting).
        var handle = source.Subscribe([Sub("ELN")]);
        handle.Dispose();
        await WaitUntilAsync(recorder, () => recorder.Greetings.Count == 1, ct);
    }
}
