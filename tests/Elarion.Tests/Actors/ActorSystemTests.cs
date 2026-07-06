using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Actors;

public sealed class ActorSystemTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyedActor_ConcurrentCalls_ExecuteSequentially() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var counter = actors.Get<ICounter>("a");

        // The increment is a read-await-write race when run concurrently; the mailbox must
        // serialize it without any lock in the actor.
        var calls = Enumerable.Range(0, 100).Select(_ => counter.Increment(TestToken).AsTask()).ToArray();
        await Task.WhenAll(calls);

        (await counter.Current(TestToken)).Should().Be(100);
    }

    [Fact]
    public async Task KeyedActor_DistinctKeys_GetDistinctActivations() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();

        await actors.Get<ICounter>("a").Increment(TestToken);
        await actors.Get<ICounter>("a").Increment(TestToken);
        await actors.Get<ICounter>("b").Increment(TestToken);

        (await actors.Get<ICounter>("a").Current(TestToken)).Should().Be(2);
        (await actors.Get<ICounter>("b").Current(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task SingletonActor_ResolvesWithoutKey() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();

        await actors.Get<IGreeter>().SetGreeting("Servus", TestToken);
        (await actors.Get<IGreeter>().Greet("Elarion", TestToken)).Should().Be("Servus Elarion");
    }

    [Fact]
    public async Task Calls_PreserveFifoOrder() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var recorder = actors.Get<IRecorder>("r");

        var calls = Enumerable.Range(0, 50).Select(i => recorder.Record(i, TestToken).AsTask()).ToArray();
        await Task.WhenAll(calls);

        (await recorder.Recorded(TestToken)).Should().BeInAscendingOrder().And.HaveCount(50);
    }

    [Fact]
    public async Task ActorException_PropagatesWithActorSideStackTrace() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var thrower = actors.Get<IThrower>("t");

        var act = async () => await thrower.Boom(TestToken);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        // The await rethrows the original actor-side exception object, so the developer sees the
        // frame inside the actor method — the cross-mailbox stack-trace story.
        assertion.Which.StackTrace.Should().Contain(nameof(ThrowerActor.Boom));
    }

    [Fact]
    public async Task CallerCancellation_WhileQueued_SkipsExecution() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<GateService>();
        var blocking = actors.Get<IBlocking>("b");

        var running = blocking.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        using var cts = new CancellationTokenSource();
        var queued = blocking.Count(cts.Token).AsTask();
        cts.Cancel();

        var act = async () => await queued;
        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.Release();
        await running.WaitAsync(WaitTimeout, TestToken);
        // The canceled call never executed.
        (await blocking.Executions(TestToken)).Should().Be(0);
    }

    [Fact]
    public async Task CallTimeout_ElapsedOnFakeClock_FailsWithTimeoutException() {
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<GateService>();
        var blocking = actors.Get<IBlocking>("b");

        var call = blocking.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        time.Advance(ActorOptions.DefaultCallTimeout + TimeSpan.FromSeconds(1));

        var act = async () => await call.WaitAsync(WaitTimeout, TestToken);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*deadlock backstop*");

        gate.Release();

        // A timed-out call's cancellation source must not poison later calls (it is disposed, not
        // recycled into the pool): subsequent calls on the same actor complete normally.
        await blocking.Count(TestToken).AsTask().WaitAsync(WaitTimeout, TestToken);
        (await blocking.Executions(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task PooledCancellationSources_RecycleAcrossManySequentialCalls() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var counter = actors.Get<ICounter>("a");

        // Every call arms the default call timeout on a pooled source and returns it via TryReset;
        // 500 sequential calls hammer the recycle path. A stale timeout or a contaminated recycled
        // source would surface as a spurious TimeoutException or cancellation here.
        for (var i = 0; i < 500; i++) {
            await counter.Increment(TestToken);
        }

        (await counter.Current(TestToken)).Should().Be(500);
    }

    [Fact]
    public async Task IdleActivation_IsPassivated_AndNextCallReactivates() {
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var actors = provider.GetRequiredService<IActorSystem>();
        var lifecycle = provider.GetRequiredService<LifecycleRecorder>();
        var counter = actors.Get<ICounter>("a");

        await counter.Increment(TestToken);
        lifecycle.Activations.Should().Be(1);

        // Advance in steps until the passivation is observed: a single jump can race the timer
        // re-arm that happens right after the call completes (the timer would then be scheduled
        // relative to the already-advanced clock and never fire).
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(1), () => lifecycle.Deactivations == 1);

        // Passivation dropped the in-memory state; the next call re-activates fresh.
        (await counter.Current(TestToken)).Should().Be(0);
        lifecycle.Activations.Should().Be(2);
    }

    [Fact]
    public async Task ReentrantActor_InterleavesWhileAwaiting_ButStaysSingleThreaded() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<GateService>();
        var reentrant = actors.Get<IReentrantProbe>("r");

        var slow = reentrant.Slow(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        // A non-reentrant mailbox would queue this behind Slow; the reentrant one interleaves it.
        await reentrant.Fast(TestToken).AsTask().WaitAsync(WaitTimeout, TestToken);

        gate.Release();
        await slow.WaitAsync(WaitTimeout, TestToken);

        (await reentrant.Events(TestToken)).Should().Equal("slow-start", "fast", "slow-end");
    }

    [Fact]
    public async Task NonReentrantActor_QueuesBehindAwaitingCall() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<GateService>();
        var blocking = actors.Get<IBlocking>("b");

        var slow = blocking.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        var queued = blocking.Count(TestToken).AsTask();
        await Task.Delay(50, TestToken);
        queued.IsCompleted.Should().BeFalse();

        gate.Release();
        await Task.WhenAll(slow, queued).WaitAsync(WaitTimeout, TestToken);
        (await blocking.Executions(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task Shutdown_DrainsMailboxes_AndDeactivates() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var lifecycle = provider.GetRequiredService<LifecycleRecorder>();
        var counter = actors.Get<ICounter>("a");

        var calls = Enumerable.Range(0, 20).Select(_ => counter.Increment(TestToken).AsTask()).ToArray();
        await Task.WhenAll(calls);

        var hostedService = provider.GetServices<IHostedService>().Single();
        await hostedService.StopAsync(TestToken);

        lifecycle.Deactivations.Should().Be(1);

        // After shutdown, new calls are rejected instead of silently reviving state.
        var act = async () => await counter.Increment(TestToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopping*");
    }

    [Fact]
    public async Task Activation_ResolvesDependenciesFromOwnScope() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();

        var first = await actors.Get<IScopeProbe>("a").ScopeId(TestToken);
        var second = await actors.Get<IScopeProbe>("b").ScopeId(TestToken);

        // Each activation owns a DI scope, so scoped dependencies are per-activation.
        first.Should().NotBe(second);
    }

    private static ServiceProvider CreateProvider(FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        services.AddSingleton<LifecycleRecorder>();
        services.AddSingleton<GateService>();
        services.AddScoped<ScopedProbeDependency>();
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<CounterActor, string, ICounter> {
            Name = "Counter",
            Options = new ActorOptions(),
            Activator = static (sp, context) => new CounterActor(context, sp.GetRequiredService<LifecycleRecorder>()),
            Facade = static handle => new CounterFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<GreeterActor, ActorSingletonKey, IGreeter> {
            Name = "Greeter",
            Options = new ActorOptions(),
            Activator = static (_, _) => new GreeterActor(),
            Facade = static handle => new GreeterFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<RecorderActor, string, IRecorder> {
            Name = "Recorder",
            Options = new ActorOptions(),
            Activator = static (_, _) => new RecorderActor(),
            Facade = static handle => new RecorderFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<ThrowerActor, string, IThrower> {
            Name = "Thrower",
            Options = new ActorOptions(),
            Activator = static (_, _) => new ThrowerActor(),
            Facade = static handle => new ThrowerFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<BlockingActor, string, IBlocking> {
            Name = "Blocking",
            Options = new ActorOptions(),
            Activator = static (sp, _) => new BlockingActor(sp.GetRequiredService<GateService>()),
            Facade = static handle => new BlockingFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<ReentrantProbeActor, string, IReentrantProbe> {
            Name = "ReentrantProbe",
            Options = new ActorOptions { Reentrant = true },
            Activator = static (sp, _) => new ReentrantProbeActor(sp.GetRequiredService<GateService>()),
            Facade = static handle => new ReentrantProbeFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<ScopeProbeActor, string, IScopeProbe> {
            Name = "ScopeProbe",
            Options = new ActorOptions(),
            Activator = static (sp, _) => new ScopeProbeActor(sp.GetRequiredService<ScopedProbeDependency>()),
            Facade = static handle => new ScopeProbeFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition) {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > WaitTimeout) {
                throw new TimeoutException("The expected actor state was not reached in time.");
            }

            time.Advance(step);
            await Task.Delay(10, TestToken);
        }
    }

    public sealed class LifecycleRecorder {
        private int _activations;
        private int _deactivations;

        public int Activations => Volatile.Read(ref _activations);
        public int Deactivations => Volatile.Read(ref _deactivations);

        public void RecordActivation() => Interlocked.Increment(ref _activations);
        public void RecordDeactivation() => Interlocked.Increment(ref _deactivations);
    }

    public sealed class GateService {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Gate => _gate.Task;

        public void Release() => _gate.TrySetResult();
    }

    public sealed class ScopedProbeDependency {
        public Guid Id { get; } = Guid.CreateVersion7();
    }

    // --- Counter (keyed, lifecycle-aware) ---

    public interface ICounter : IActorFacade<string> {
        ValueTask<int> Increment(CancellationToken cancellationToken = default);
        ValueTask<int> Current(CancellationToken cancellationToken = default);
    }

    public sealed class CounterActor(IActorContext<string> context, LifecycleRecorder recorder)
        : IActorLifecycle {
        private int _count;

        public string Key => context.Key;

        public ValueTask OnActivateAsync(CancellationToken cancellationToken) {
            recorder.RecordActivation();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
            recorder.RecordDeactivation();
            return ValueTask.CompletedTask;
        }

        public async Task<int> Increment(CancellationToken cancellationToken) {
            var read = _count;
            await Task.Yield();
            _count = read + 1;
            return _count;
        }

        public Task<int> Current(CancellationToken cancellationToken) => Task.FromResult(_count);
    }

    private sealed class CounterFacade(ActorHandle<CounterActor> handle) : ICounter {
        public ValueTask<int> Increment(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new IncrementItem(), cancellationToken);

        public ValueTask<int> Current(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CurrentItem(), cancellationToken);

        private sealed class IncrementItem : ActorWorkItem<CounterActor, int> {
            public override string MethodName => "Increment";

            protected override async ValueTask<int> InvokeAsync(CounterActor actor, CancellationToken cancellationToken) =>
                await actor.Increment(cancellationToken).ConfigureAwait(false);
        }

        private sealed class CurrentItem : ActorWorkItem<CounterActor, int> {
            public override string MethodName => "Current";

            protected override async ValueTask<int> InvokeAsync(CounterActor actor, CancellationToken cancellationToken) =>
                await actor.Current(cancellationToken).ConfigureAwait(false);
        }
    }

    // --- Greeter (singleton) ---

    public interface IGreeter : IActorFacade {
        ValueTask SetGreeting(string greeting, CancellationToken cancellationToken = default);
        ValueTask<string> Greet(string name, CancellationToken cancellationToken = default);
    }

    public sealed class GreeterActor {
        private string _greeting = "Hello";

        public Task SetGreeting(string greeting) {
            _greeting = greeting;
            return Task.CompletedTask;
        }

        public Task<string> Greet(string name) => Task.FromResult($"{_greeting} {name}");
    }

    private sealed class GreeterFacade(ActorHandle<GreeterActor> handle) : IGreeter {
        public ValueTask SetGreeting(string greeting, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new SetGreetingItem(greeting), cancellationToken);

        public ValueTask<string> Greet(string name, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new GreetItem(name), cancellationToken);

        private sealed class SetGreetingItem(string greeting) : ActorWorkItem<GreeterActor, Unit> {
            public override string MethodName => "SetGreeting";

            protected override async ValueTask<Unit> InvokeAsync(GreeterActor actor, CancellationToken cancellationToken) {
                await actor.SetGreeting(greeting).ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class GreetItem(string name) : ActorWorkItem<GreeterActor, string> {
            public override string MethodName => "Greet";

            protected override async ValueTask<string> InvokeAsync(GreeterActor actor, CancellationToken cancellationToken) =>
                await actor.Greet(name).ConfigureAwait(false);
        }
    }

    // --- Recorder (FIFO order probe) ---

    public interface IRecorder : IActorFacade<string> {
        ValueTask Record(int value, CancellationToken cancellationToken = default);
        ValueTask<int[]> Recorded(CancellationToken cancellationToken = default);
    }

    public sealed class RecorderActor {
        private readonly List<int> _values = [];

        public async Task Record(int value) {
            await Task.Yield();
            _values.Add(value);
        }

        public Task<int[]> Recorded() => Task.FromResult(_values.ToArray());
    }

    private sealed class RecorderFacade(ActorHandle<RecorderActor> handle) : IRecorder {
        public ValueTask Record(int value, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new RecordItem(value), cancellationToken);

        public ValueTask<int[]> Recorded(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new RecordedItem(), cancellationToken);

        private sealed class RecordItem(int value) : ActorWorkItem<RecorderActor, Unit> {
            public override string MethodName => "Record";

            protected override async ValueTask<Unit> InvokeAsync(RecorderActor actor, CancellationToken cancellationToken) {
                await actor.Record(value).ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class RecordedItem : ActorWorkItem<RecorderActor, int[]> {
            public override string MethodName => "Recorded";

            protected override async ValueTask<int[]> InvokeAsync(RecorderActor actor, CancellationToken cancellationToken) =>
                await actor.Recorded().ConfigureAwait(false);
        }
    }

    // --- Thrower (stack-trace probe) ---

    public interface IThrower : IActorFacade<string> {
        ValueTask Boom(CancellationToken cancellationToken = default);
    }

    public sealed class ThrowerActor {
        public async Task Boom() {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ThrowerFacade(ActorHandle<ThrowerActor> handle) : IThrower {
        public ValueTask Boom(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new BoomItem(), cancellationToken);

        private sealed class BoomItem : ActorWorkItem<ThrowerActor, Unit> {
            public override string MethodName => "Boom";

            protected override async ValueTask<Unit> InvokeAsync(ThrowerActor actor, CancellationToken cancellationToken) {
                await actor.Boom().ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }

    // --- Blocking (cancellation / timeout / non-reentrancy probe) ---

    public interface IBlocking : IActorFacade<string> {
        ValueTask WaitForGate(CancellationToken cancellationToken = default);
        ValueTask Count(CancellationToken cancellationToken = default);
        ValueTask<int> Executions(CancellationToken cancellationToken = default);
    }

    public sealed class BlockingActor(GateService gate) {
        private int _executions;

        public async Task WaitForGate() {
            gate.Started.TrySetResult();
            await gate.Gate;
        }

        public Task Count() {
            _executions++;
            return Task.CompletedTask;
        }

        public Task<int> Executions() => Task.FromResult(_executions);
    }

    private sealed class BlockingFacade(ActorHandle<BlockingActor> handle) : IBlocking {
        public ValueTask WaitForGate(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new WaitForGateItem(), cancellationToken);

        public ValueTask Count(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CountItem(), cancellationToken);

        public ValueTask<int> Executions(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ExecutionsItem(), cancellationToken);

        private sealed class WaitForGateItem : ActorWorkItem<BlockingActor, Unit> {
            public override string MethodName => "WaitForGate";

            protected override async ValueTask<Unit> InvokeAsync(BlockingActor actor, CancellationToken cancellationToken) {
                await actor.WaitForGate().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class CountItem : ActorWorkItem<BlockingActor, Unit> {
            public override string MethodName => "Count";

            protected override async ValueTask<Unit> InvokeAsync(BlockingActor actor, CancellationToken cancellationToken) {
                await actor.Count().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class ExecutionsItem : ActorWorkItem<BlockingActor, int> {
            public override string MethodName => "Executions";

            protected override async ValueTask<int> InvokeAsync(BlockingActor actor, CancellationToken cancellationToken) =>
                await actor.Executions().ConfigureAwait(false);
        }
    }

    // --- Reentrant probe ---

    public interface IReentrantProbe : IActorFacade<string> {
        ValueTask Slow(CancellationToken cancellationToken = default);
        ValueTask Fast(CancellationToken cancellationToken = default);
        ValueTask<string[]> Events(CancellationToken cancellationToken = default);
    }

    public sealed class ReentrantProbeActor(GateService gate) {
        private readonly List<string> _events = [];

        public async Task Slow() {
            _events.Add("slow-start");
            gate.Started.TrySetResult();
            await gate.Gate;
            _events.Add("slow-end");
        }

        public Task Fast() {
            _events.Add("fast");
            return Task.CompletedTask;
        }

        public Task<string[]> Events() => Task.FromResult(_events.ToArray());
    }

    private sealed class ReentrantProbeFacade(ActorHandle<ReentrantProbeActor> handle) : IReentrantProbe {
        public ValueTask Slow(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new SlowItem(), cancellationToken);

        public ValueTask Fast(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new FastItem(), cancellationToken);

        public ValueTask<string[]> Events(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new EventsItem(), cancellationToken);

        private sealed class SlowItem : ActorWorkItem<ReentrantProbeActor, Unit> {
            public override string MethodName => "Slow";

            protected override async ValueTask<Unit> InvokeAsync(ReentrantProbeActor actor, CancellationToken cancellationToken) {
                await actor.Slow().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class FastItem : ActorWorkItem<ReentrantProbeActor, Unit> {
            public override string MethodName => "Fast";

            protected override async ValueTask<Unit> InvokeAsync(ReentrantProbeActor actor, CancellationToken cancellationToken) {
                await actor.Fast().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class EventsItem : ActorWorkItem<ReentrantProbeActor, string[]> {
            public override string MethodName => "Events";

            protected override async ValueTask<string[]> InvokeAsync(ReentrantProbeActor actor, CancellationToken cancellationToken) =>
                await actor.Events().ConfigureAwait(false);
        }
    }

    // --- Scope probe ---

    public interface IScopeProbe : IActorFacade<string> {
        ValueTask<Guid> ScopeId(CancellationToken cancellationToken = default);
    }

    public sealed class ScopeProbeActor(ScopedProbeDependency dependency) {
        public Task<Guid> ScopeId() => Task.FromResult(dependency.Id);
    }

    private sealed class ScopeProbeFacade(ActorHandle<ScopeProbeActor> handle) : IScopeProbe {
        public ValueTask<Guid> ScopeId(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ScopeIdItem(), cancellationToken);

        private sealed class ScopeIdItem : ActorWorkItem<ScopeProbeActor, Guid> {
            public override string MethodName => "ScopeId";

            protected override async ValueTask<Guid> InvokeAsync(ScopeProbeActor actor, CancellationToken cancellationToken) =>
                await actor.ScopeId().ConfigureAwait(false);
        }
    }
}
