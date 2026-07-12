using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Actors.Runtime;
using Elarion.Streams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// The ADR-0052 actor-stream contract: a facade method returning <c>IAsyncEnumerable&lt;T&gt;</c> attaches
/// through the mailbox (serialized with ordinary turns), the enumeration runs off the mailbox in publish
/// order, each enumeration is its own subscription, and completing the hub on deactivation ends consumers.
/// The facade/work-item classes mirror what <c>ActorRegistrationGenerator</c> emits for the stream shape.
/// </summary>
public sealed class ActorStreamTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamMethod_GreetsWithTheCurrentValue_ThenObservesTurnsInOrder() {
        await using var provider = CreateProvider();
        var ticker = provider.GetRequiredService<IActorSystem>().Get<ITicker>("ELN");

        await ticker.Apply(1, TestToken);
        await ticker.Apply(2, TestToken);

        var observed = new List<StreamItem<int>>();
        await foreach (var item in ticker.Watch(resumeAfter: null, TestToken).WithCancellation(TestToken)) {
            observed.Add(item);
            if (observed.Count == 1) {
                // The greeting (latest = 2) arrived; now interleave live turns.
                await ticker.Apply(3, TestToken);
                await ticker.Apply(4, TestToken);
                await ticker.Close(TestToken);
            }
        }

        observed.Select(static i => i.Value).Should().Equal(2, 3, 4);
        observed.Select(static i => i.Sequence).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task StreamMethod_ResumesAfterASequence() {
        await using var provider = CreateProvider();
        var ticker = provider.GetRequiredService<IActorSystem>().Get<ITicker>("ELN");

        for (var i = 1; i <= 5; i++) {
            await ticker.Apply(i, TestToken);
        }

        await ticker.Close(TestToken);

        var observed = new List<long>();
        await foreach (var item in ticker.Watch(resumeAfter: 3, TestToken).WithCancellation(TestToken)) {
            observed.Add(item.Sequence);
        }

        observed.Should().Equal(4, 5);
    }

    [Fact]
    public async Task EachEnumeration_IsItsOwnSubscription() {
        await using var provider = CreateProvider();
        var ticker = provider.GetRequiredService<IActorSystem>().Get<ITicker>("ELN");
        await ticker.Apply(7, TestToken);
        await ticker.Close(TestToken);

        var stream = ticker.Watch(resumeAfter: null, TestToken);
        var first = new List<int>();
        await foreach (var item in stream.WithCancellation(TestToken)) {
            first.Add(item.Value);
        }

        var second = new List<int>();
        await foreach (var item in stream.WithCancellation(TestToken)) {
            second.Add(item.Value);
        }

        first.Should().Equal(7);
        second.Should().Equal(first, "each enumeration runs its own attach turn and subscription");
    }

    [Fact]
    public async Task FacadeToken_CancelsTheStream() {
        await using var provider = CreateProvider();
        var ticker = provider.GetRequiredService<IActorSystem>().Get<ITicker>("ELN");
        await ticker.Apply(1, TestToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var act = async () => {
            await foreach (var _ in ticker.Watch(resumeAfter: null, cts.Token)) {
                await cts.CancelAsync(); // cancel after the greeting; the live wait must observe it
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LiveEnumeration_RetainsTheActivation_AgainstIdlePassivation() {
        // The streaming-only shape: no turns arrive while consumers sit attached — without retention
        // the idle timer would passivate mid-stream and the sequence would restart (a new epoch).
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var idle = TimeSpan.FromSeconds(1);
        var deactivations = new DeactivationCounter();
        await using var provider = CreateProvider(options: new ActorOptions { IdleTimeout = idle },
            deactivations: deactivations, timeProvider: time);
        var ticker = provider.GetRequiredService<IActorSystem>().Get<ITicker>("ELN");
        await ticker.Apply(1, TestToken);

        await using var enumerator = ticker.Watch(resumeAfter: null, TestToken).GetAsyncEnumerator(TestToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue(); // the greeting — the attach turn ran

        // Idle fires many windows over: the live enumeration must keep the activation alive.
        for (var i = 0; i < 5; i++) {
            time.Advance(idle + TimeSpan.FromMilliseconds(50));
            await Task.Delay(10, TestToken);
        }

        deactivations.Count.Should().Be(0, "a live stream enumeration retains the activation");

        // The last consumer leaves: the idle window restarts and passivation resumes.
        await enumerator.DisposeAsync();
        await AdvanceUntilAsync(time, idle + TimeSpan.FromMilliseconds(50), () => deactivations.Count == 1);
    }

    private static async Task AdvanceUntilAsync(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider time, TimeSpan step, Func<bool> condition) {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(60)) {
                throw new TimeoutException("The expected actor state was not reached in time.");
            }

            time.Advance(step);
            await Task.Delay(10, TestToken);
        }
    }

    public sealed class DeactivationCounter {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record() => Interlocked.Increment(ref _count);
    }

    private static ServiceProvider CreateProvider(
        ActorOptions? options = null, DeactivationCounter? deactivations = null, TimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        if (timeProvider is not null) {
            services.AddSingleton(timeProvider);
        }

        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<TickerActor, string, ITicker> {
            Name = "Ticker",
            Options = options ?? new ActorOptions(),
            Activator = (_, _) => new TickerActor(deactivations),
            Facade = static handle => new TickerFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    public interface ITicker : IActorFacade<string> {
        Task Apply(int value, CancellationToken cancellationToken = default);
        Task Close(CancellationToken cancellationToken = default);
        IAsyncEnumerable<StreamItem<int>> Watch(long? resumeAfter, CancellationToken cancellationToken = default);
    }

    public sealed class TickerActor(DeactivationCounter? deactivations = null) : IActorLifecycle {
        private readonly StreamHub<int> _hub = new(new StreamHubOptions { ReplayCapacity = 16 });

        public ValueTask OnActivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
            _hub.Complete();
            deactivations?.Record();
            return ValueTask.CompletedTask;
        }

        public async Task Apply(int value, CancellationToken cancellationToken) =>
            await _hub.PublishAsync(value, cancellationToken);

        public Task Close(CancellationToken cancellationToken) {
            _hub.Complete();
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<StreamItem<int>> Watch(long? resumeAfter) =>
            _hub.SubscribeSequenced(new StreamSubscribeOptions { ResumeAfterSequence = resumeAfter });
    }

    // Mirrors the generated facade: Task shapes pass through; the stream shape defers the attach turn
    // until enumeration via ActorStreams.Defer.
    private sealed class TickerFacade(ActorHandle<TickerActor> handle) : ITicker {
        public Task Apply(int value, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ApplyItem(value), cancellationToken).AsTask();

        public Task Close(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CloseItem(), cancellationToken).AsTask();

        public IAsyncEnumerable<StreamItem<int>> Watch(long? resumeAfter, CancellationToken cancellationToken = default) =>
            ActorStreams.Defer<StreamItem<int>>(
                elarionAttachToken => handle.InvokeAsync(new WatchItem(resumeAfter), elarionAttachToken),
                cancellationToken);

        private sealed class ApplyItem(int value) : ActorWorkItem<TickerActor, Elarion.Abstractions.Results.Unit> {
            public override string MethodName => "Apply";

            protected override async ValueTask<Elarion.Abstractions.Results.Unit> InvokeAsync(
                TickerActor actor, CancellationToken cancellationToken) {
                await actor.Apply(value, cancellationToken).ConfigureAwait(false);
                return Elarion.Abstractions.Results.Unit.Value;
            }
        }

        private sealed class CloseItem : ActorWorkItem<TickerActor, Elarion.Abstractions.Results.Unit> {
            public override string MethodName => "Close";

            protected override async ValueTask<Elarion.Abstractions.Results.Unit> InvokeAsync(
                TickerActor actor, CancellationToken cancellationToken) {
                await actor.Close(cancellationToken).ConfigureAwait(false);
                return Elarion.Abstractions.Results.Unit.Value;
            }
        }

        private sealed class WatchItem(long? resumeAfter) : ActorWorkItem<TickerActor, IAsyncEnumerable<StreamItem<int>>> {
            public override string MethodName => "Watch";

            protected override ValueTask<IAsyncEnumerable<StreamItem<int>>> InvokeAsync(
                TickerActor actor, CancellationToken cancellationToken) =>
                new(ActorStreams.RetainWhileEnumerating(actor.Watch(resumeAfter), RetainActivation()));
        }
    }
}
