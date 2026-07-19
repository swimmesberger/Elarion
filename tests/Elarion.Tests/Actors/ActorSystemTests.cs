using System.Collections.Concurrent;
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
    public async Task ForeignOperationCanceledException_FaultsTheCall_InsteadOfMasqueradingAsCanceled() {
        await using var provider = CreateProvider();
        var thrower = provider.GetRequiredService<IActorSystem>().Get<IThrower>("t");

        var call = thrower.BoomWithForeignCancellation(TestToken).AsTask();
        var act = async () => await call.WaitAsync(WaitTimeout, TestToken);
        await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("foreign timeout*");

        // Nothing of ours was cancelled (an HttpClient-internal timeout OCE is the archetype), so
        // the call must surface as a fault with actor-side provenance — not benign cancellation.
        call.IsFaulted.Should().BeTrue();
        call.IsCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task ThrowingRecycleOverride_DoesNotKillTheMailbox() {
        await using var provider = CreateProvider();
        var probe = provider.GetRequiredService<IActorSystem>().Get<IRecycleProbe>("r");

        (await probe.Increment(TestToken)).Should().Be(1);
        // A Recycle escaping into the pump would tear the activation down between calls; the
        // preserved in-memory count proves the same activation kept serving.
        (await probe.Increment(TestToken)).Should().Be(2);
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
        for (var i = 0; i < 500; i++) await counter.Increment(TestToken);

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
    [Trait("Category", "Concurrency")]
    public async Task PooledWorkItems_UnderConcurrentReuse_ReturnEachCallersOwnResult() {
        // Recycle-safety guard: PooledEchoItem returns itself to a pool after each call (what the
        // generated facade would do). With thousands of distinct-valued calls churning the pool, the
        // capture-the-Task-before-enqueue contract must hold — a recycled item re-initialized for a
        // new caller must never surface its value to the previous caller. Cross-talk shows up as a
        // result multiset that is not exactly {0..n-1}.
        await using var provider = CreateProvider();
        var echo = provider.GetRequiredService<IActorSystem>().Get<IPooledEcho>("p");

        const int n = 5000;
        var results = await Task.WhenAll(
            Enumerable.Range(0, n).Select(i => echo.Echo(i, TestToken).AsTask()));

        results.Should().BeEquivalentTo(Enumerable.Range(0, n));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task CallerCancellation_RacingCompletion_ResolvesExactlyOnce() {
        // Ahead-of-change guard for the value-task-source completion rework: the caller-cancel
        // registration and the actor setting the result fire near-simultaneously. Today's
        // TaskCompletionSource.TrySet* is idempotent; a value-task source's SetResult/SetException
        // throw on a second set, so the completion must be claimed by exactly one racer. A broken
        // guard surfaces here as an InvalidOperationException escaping the OCE catch, or as a call
        // that never resolves (successes + cancellations would not add up).
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var counter = actors.Get<ICounter>("hot");

        const int iterations = 2000;
        var successes = 0;
        var cancellations = 0;
        for (var i = 0; i < iterations; i++) {
            using var cts = new CancellationTokenSource();
            var call = counter.Increment(cts.Token).AsTask();
            var cancel = Task.Run(() => cts.Cancel(), TestToken);
            try {
                await call;
                successes++;
            }
            catch (OperationCanceledException) {
                cancellations++;
            }

            await cancel;
        }

        (successes + cancellations).Should().Be(iterations, "every call resolves exactly once");
        // The actor survives the barrage and still serves calls.
        await counter.Current(TestToken);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task ConcurrentCalls_RacingRapidPassivation_ExecuteExactlyOnce() {
        // Directly stresses the lock-free mailbox: a tiny idle timeout makes the cell passivate in
        // every gap between call bursts, so enqueues constantly race the idle timer closing the
        // cell. The packed closed/pending word must make "reserve a slot" and "close only when zero
        // pending" mutually exclusive — otherwise a call is dropped (tally too low), double-run
        // (too high), or a caller observes a spurious failure. The tally survives passivation; the
        // actor's in-memory count does not, so it is the sink we assert on.
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        var recorder = new LifecycleRecorder();
        services.AddSingleton(recorder);
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<CounterActor, string, ICounter> {
            Name = "Counter",
            Options = new ActorOptions { IdleTimeout = TimeSpan.FromMilliseconds(1) },
            Activator = static (sp, context) => new CounterActor(context, sp.GetRequiredService<LifecycleRecorder>()),
            Facade = static handle => new CounterFacade(handle)
        });
        await using var provider = services.BuildServiceProvider();
        var counter = provider.GetRequiredService<IActorSystem>().Get<ICounter>("hot");

        const int rounds = 150;
        const int batch = 8;
        for (var round = 0; round < rounds; round++) {
            var calls = Enumerable.Range(0, batch)
                .Select(_ => counter.Increment(TestToken).AsTask())
                .ToArray();
            await Task.WhenAll(calls).WaitAsync(WaitTimeout, TestToken);
            // Let the 1 ms idle timer fire in the gap so the next burst races a fresh passivation.
            await Task.Delay(2, TestToken);
        }

        recorder.Executions.Should().Be(rounds * batch);
        // The run only proves the race window was exercised if passivation actually happened.
        recorder.Deactivations.Should().BeGreaterThan(0);
        // Every passivation is followed by a reactivation; the final activation may or may not have
        // passivated yet by the time we read, so activations is deactivations or one more.
        recorder.Activations.Should().BeInRange(recorder.Deactivations, recorder.Deactivations + 1);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Shutdown_RacingFirstCalls_NeverLeaksALiveActivation() {
        // TOCTOU guard: a caller past the _stopping pre-check may GetOrAdd + start a fresh
        // activation after StopAsync snapshotted the cell map — that cell would escape the drain
        // fan-out. The enqueue path re-checks and drains such a cell itself, so once StopAsync and
        // every caller have settled, no activation (and no DI scope) may still be alive.
        const int rounds = 150;
        for (var round = 0; round < rounds; round++) {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            var recorder = new LifecycleRecorder();
            services.AddSingleton(recorder);
            services.AddElarionActorSystem();
            services.AddElarionActor(new ActorRegistration<CounterActor, string, ICounter> {
                Name = "Counter",
                Options = new ActorOptions(),
                Activator = static (sp, context) =>
                    new CounterActor(context, sp.GetRequiredService<LifecycleRecorder>()),
                Facade = static handle => new CounterFacade(handle)
            });
            await using var provider = services.BuildServiceProvider();
            var actors = provider.GetRequiredService<IActorSystem>();
            var hostedService = provider.GetServices<IHostedService>().Single();

            var calls = Enumerable.Range(0, 4).Select(i => Task.Run(async () => {
                try {
                    await actors.Get<ICounter>("k" + i).Increment(TestToken);
                }
                catch (InvalidOperationException) {
                    // The system was stopping: the call was rejected, and any freshly created
                    // activation was drained before the rejection surfaced.
                }
            }, TestToken)).ToArray();
            var stop = hostedService.StopAsync(CancellationToken.None);
            await Task.WhenAll(calls).WaitAsync(WaitTimeout, TestToken);
            await stop.WaitAsync(WaitTimeout, TestToken);

            recorder.Activations.Should().Be(
                recorder.Deactivations, $"no activation may outlive shutdown (round {round})");
        }
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
        services.AddElarionActor(new ActorRegistration<PooledEchoActor, string, IPooledEcho> {
            Name = "PooledEcho",
            Options = new ActorOptions(),
            Activator = static (_, _) => new PooledEchoActor(),
            Facade = static handle => new PooledEchoFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<RecycleProbeActor, string, IRecycleProbe> {
            Name = "RecycleProbe",
            Options = new ActorOptions(),
            Activator = static (_, _) => new RecycleProbeActor(),
            Facade = static handle => new RecycleProbeFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition) {
        var stopwatch = Stopwatch.StartNew();
        while (!condition()) {
            if (stopwatch.Elapsed > WaitTimeout)
                throw new TimeoutException("The expected actor state was not reached in time.");

            time.Advance(step);
            await Task.Delay(10, TestToken);
        }
    }

    public sealed class LifecycleRecorder {
        private int _activations;
        private int _deactivations;
        private int _executions;

        public int Activations => Volatile.Read(ref _activations);
        public int Deactivations => Volatile.Read(ref _deactivations);

        // A sink that survives passivation (unlike the actor's in-memory count), so a stress test
        // can assert every queued call ran exactly once even as activations come and go.
        public int Executions => Volatile.Read(ref _executions);

        public void RecordActivation() {
            Interlocked.Increment(ref _activations);
        }

        public void RecordDeactivation() {
            Interlocked.Increment(ref _deactivations);
        }

        public void RecordExecution() {
            Interlocked.Increment(ref _executions);
        }
    }

    public sealed class GateService {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Gate => _gate.Task;

        public void Release() {
            _gate.TrySetResult();
        }
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
            recorder.RecordExecution();
            return _count;
        }

        public Task<int> Current(CancellationToken cancellationToken) {
            return Task.FromResult(_count);
        }
    }

    private sealed class CounterFacade(ActorHandle<CounterActor> handle) : ICounter {
        public ValueTask<int> Increment(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new IncrementItem(), cancellationToken);
        }

        public ValueTask<int> Current(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new CurrentItem(), cancellationToken);
        }

        private sealed class IncrementItem : ActorWorkItem<CounterActor, int> {
            public override string MethodName => "Increment";

            protected override async ValueTask<int>
                InvokeAsync(CounterActor actor, CancellationToken cancellationToken) {
                return await actor.Increment(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class CurrentItem : ActorWorkItem<CounterActor, int> {
            public override string MethodName => "Current";

            protected override async ValueTask<int>
                InvokeAsync(CounterActor actor, CancellationToken cancellationToken) {
                return await actor.Current(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // --- PooledEcho (exercises pooled/recycled work items) ---

    public interface IPooledEcho : IActorFacade<string> {
        ValueTask<int> Echo(int value, CancellationToken cancellationToken = default);
    }

    public sealed class PooledEchoActor {
        public async Task<int> Echo(int value) {
            await Task.Yield();
            return value;
        }
    }

    private sealed class PooledEchoFacade(ActorHandle<PooledEchoActor> handle) : IPooledEcho {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(PooledEchoItem.Rent(value), cancellationToken);
        }
    }

    // Mirrors what a pooling generator would emit: rented per call, self-returned via the Recycle hook.
    private sealed class PooledEchoItem : ActorWorkItem<PooledEchoActor, int> {
        private static readonly ConcurrentQueue<PooledEchoItem> Pool = new();
        private int _value;

        public override string MethodName => "Echo";

        public static PooledEchoItem Rent(int value) {
            if (!Pool.TryDequeue(out var item)) item = new PooledEchoItem();

            item._value = value;
            return item;
        }

        protected override void Recycle() {
            Pool.Enqueue(this);
        }

        protected override async ValueTask<int>
            InvokeAsync(PooledEchoActor actor, CancellationToken cancellationToken) {
            return await actor.Echo(_value).ConfigureAwait(false);
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

        public Task<string> Greet(string name) {
            return Task.FromResult($"{_greeting} {name}");
        }
    }

    private sealed class GreeterFacade(ActorHandle<GreeterActor> handle) : IGreeter {
        public ValueTask SetGreeting(string greeting, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new SetGreetingItem(greeting), cancellationToken);
        }

        public ValueTask<string> Greet(string name, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new GreetItem(name), cancellationToken);
        }

        private sealed class SetGreetingItem(string greeting) : ActorWorkItem<GreeterActor, Unit> {
            public override string MethodName => "SetGreeting";

            protected override async ValueTask<Unit> InvokeAsync(GreeterActor actor,
                CancellationToken cancellationToken) {
                await actor.SetGreeting(greeting).ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class GreetItem(string name) : ActorWorkItem<GreeterActor, string> {
            public override string MethodName => "Greet";

            protected override async ValueTask<string> InvokeAsync(GreeterActor actor,
                CancellationToken cancellationToken) {
                return await actor.Greet(name).ConfigureAwait(false);
            }
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

        public Task<int[]> Recorded() {
            return Task.FromResult(_values.ToArray());
        }
    }

    private sealed class RecorderFacade(ActorHandle<RecorderActor> handle) : IRecorder {
        public ValueTask Record(int value, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new RecordItem(value), cancellationToken);
        }

        public ValueTask<int[]> Recorded(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new RecordedItem(), cancellationToken);
        }

        private sealed class RecordItem(int value) : ActorWorkItem<RecorderActor, Unit> {
            public override string MethodName => "Record";

            protected override async ValueTask<Unit> InvokeAsync(RecorderActor actor,
                CancellationToken cancellationToken) {
                await actor.Record(value).ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class RecordedItem : ActorWorkItem<RecorderActor, int[]> {
            public override string MethodName => "Recorded";

            protected override async ValueTask<int[]> InvokeAsync(RecorderActor actor,
                CancellationToken cancellationToken) {
                return await actor.Recorded().ConfigureAwait(false);
            }
        }
    }

    // --- Thrower (stack-trace / cancellation-attribution probe) ---

    public interface IThrower : IActorFacade<string> {
        ValueTask Boom(CancellationToken cancellationToken = default);
        ValueTask BoomWithForeignCancellation(CancellationToken cancellationToken = default);
    }

    public sealed class ThrowerActor {
        public async Task Boom() {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }

        public async Task BoomWithForeignCancellation() {
            await Task.Yield();
            // An OCE carrying a token unrelated to the invocation (e.g. HttpClient's internal
            // timeout source): a real fault, not a cancellation of this call.
            using var foreignSource = new CancellationTokenSource();
            foreignSource.Cancel();
            throw new OperationCanceledException("foreign timeout", foreignSource.Token);
        }
    }

    private sealed class ThrowerFacade(ActorHandle<ThrowerActor> handle) : IThrower {
        public ValueTask Boom(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new BoomItem(), cancellationToken);
        }

        public ValueTask BoomWithForeignCancellation(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new BoomWithForeignCancellationItem(), cancellationToken);
        }

        private sealed class BoomItem : ActorWorkItem<ThrowerActor, Unit> {
            public override string MethodName => "Boom";

            protected override async ValueTask<Unit> InvokeAsync(ThrowerActor actor,
                CancellationToken cancellationToken) {
                await actor.Boom().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class BoomWithForeignCancellationItem : ActorWorkItem<ThrowerActor, Unit> {
            public override string MethodName => "BoomWithForeignCancellation";

            protected override async ValueTask<Unit> InvokeAsync(ThrowerActor actor,
                CancellationToken cancellationToken) {
                await actor.BoomWithForeignCancellation().ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }

    // --- Recycle probe (a pooling subclass whose Recycle throws) ---

    public interface IRecycleProbe : IActorFacade<string> {
        ValueTask<int> Increment(CancellationToken cancellationToken = default);
    }

    public sealed class RecycleProbeActor {
        private int _count;

        public Task<int> Increment() {
            return Task.FromResult(++_count);
        }
    }

    private sealed class RecycleProbeFacade(ActorHandle<RecycleProbeActor> handle) : IRecycleProbe {
        public ValueTask<int> Increment(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new ThrowingRecycleItem(), cancellationToken);
        }

        // A buggy pooling subclass: the runtime must swallow the Recycle failure (the item is
        // simply not pooled) instead of letting it escape into the cell's pump loop.
        private sealed class ThrowingRecycleItem : ActorWorkItem<RecycleProbeActor, int> {
            public override string MethodName => "Increment";

            protected override void Recycle() {
                throw new InvalidOperationException("recycle boom");
            }

            protected override async ValueTask<int> InvokeAsync(RecycleProbeActor actor,
                CancellationToken cancellationToken) {
                return await actor.Increment().ConfigureAwait(false);
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

        public Task<int> Executions() {
            return Task.FromResult(_executions);
        }
    }

    private sealed class BlockingFacade(ActorHandle<BlockingActor> handle) : IBlocking {
        public ValueTask WaitForGate(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new WaitForGateItem(), cancellationToken);
        }

        public ValueTask Count(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new CountItem(), cancellationToken);
        }

        public ValueTask<int> Executions(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new ExecutionsItem(), cancellationToken);
        }

        private sealed class WaitForGateItem : ActorWorkItem<BlockingActor, Unit> {
            public override string MethodName => "WaitForGate";

            protected override async ValueTask<Unit> InvokeAsync(BlockingActor actor,
                CancellationToken cancellationToken) {
                await actor.WaitForGate().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class CountItem : ActorWorkItem<BlockingActor, Unit> {
            public override string MethodName => "Count";

            protected override async ValueTask<Unit> InvokeAsync(BlockingActor actor,
                CancellationToken cancellationToken) {
                await actor.Count().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class ExecutionsItem : ActorWorkItem<BlockingActor, int> {
            public override string MethodName => "Executions";

            protected override async ValueTask<int> InvokeAsync(BlockingActor actor,
                CancellationToken cancellationToken) {
                return await actor.Executions().ConfigureAwait(false);
            }
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

        public Task<string[]> Events() {
            return Task.FromResult(_events.ToArray());
        }
    }

    private sealed class ReentrantProbeFacade(ActorHandle<ReentrantProbeActor> handle) : IReentrantProbe {
        public ValueTask Slow(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new SlowItem(), cancellationToken);
        }

        public ValueTask Fast(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new FastItem(), cancellationToken);
        }

        public ValueTask<string[]> Events(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new EventsItem(), cancellationToken);
        }

        private sealed class SlowItem : ActorWorkItem<ReentrantProbeActor, Unit> {
            public override string MethodName => "Slow";

            protected override async ValueTask<Unit> InvokeAsync(ReentrantProbeActor actor,
                CancellationToken cancellationToken) {
                await actor.Slow().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class FastItem : ActorWorkItem<ReentrantProbeActor, Unit> {
            public override string MethodName => "Fast";

            protected override async ValueTask<Unit> InvokeAsync(ReentrantProbeActor actor,
                CancellationToken cancellationToken) {
                await actor.Fast().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class EventsItem : ActorWorkItem<ReentrantProbeActor, string[]> {
            public override string MethodName => "Events";

            protected override async ValueTask<string[]> InvokeAsync(ReentrantProbeActor actor,
                CancellationToken cancellationToken) {
                return await actor.Events().ConfigureAwait(false);
            }
        }
    }

    // --- Scope probe ---

    public interface IScopeProbe : IActorFacade<string> {
        ValueTask<Guid> ScopeId(CancellationToken cancellationToken = default);
    }

    public sealed class ScopeProbeActor(ScopedProbeDependency dependency) {
        public Task<Guid> ScopeId() {
            return Task.FromResult(dependency.Id);
        }
    }

    private sealed class ScopeProbeFacade(ActorHandle<ScopeProbeActor> handle) : IScopeProbe {
        public ValueTask<Guid> ScopeId(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new ScopeIdItem(), cancellationToken);
        }

        private sealed class ScopeIdItem : ActorWorkItem<ScopeProbeActor, Guid> {
            public override string MethodName => "ScopeId";

            protected override async ValueTask<Guid> InvokeAsync(ScopeProbeActor actor,
                CancellationToken cancellationToken) {
                return await actor.ScopeId().ConfigureAwait(false);
            }
        }
    }
}
