using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// Encodes the reentrancy contract of ADR-0042: non-reentrant actors run one message start-to-finish
/// (a self-call deadlocks into the call-timeout backstop); [Reentrant] actors interleave turns at
/// await points (self-calls and call cycles complete) while never executing two turns in parallel,
/// and state observed across an await may have been changed by an interleaved message.
/// </summary>
public sealed class ActorReentrancyTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReentrantActor_SelfCall_CompletesViaInterleaving() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();

        // CallSelf awaits a second message on its own mailbox. Under reentrancy the inner Noop
        // interleaves while CallSelf is awaiting, so the cycle resolves without any timeout.
        await actors.Get<IReentrantSelfCaller>("self").CallSelf(TestToken)
            .AsTask().WaitAsync(WaitTimeout, TestToken);
    }

    [Fact]
    public async Task NonReentrantActor_SelfCall_DeadlocksIntoCallTimeout() {
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(time);
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<Gate>();

        // The inner Noop queues behind the still-running CallSelf: the classic virtual-actor
        // deadlock. The call timeout is the backstop that surfaces it as an error, not a hang.
        var call = actors.Get<IBlockingSelfCaller>("self").CallSelf(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        time.Advance(ActorOptions.DefaultCallTimeout + TimeSpan.FromSeconds(1));

        var act = async () => await call.WaitAsync(WaitTimeout, TestToken);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*deadlock backstop*");
    }

    [Fact]
    public async Task ReentrantActors_CallCycle_Completes() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var a = actors.Get<IPingPong>("a");
        var b = actors.Get<IPingPong>("b");

        // Orleans' "case 3": A and B call each other simultaneously. Because both are reentrant,
        // each Ping interleaves with the pending CallOther and the cycle cannot deadlock.
        await Task.WhenAll(a.CallOther("b", TestToken).AsTask(), b.CallOther("a", TestToken).AsTask())
            .WaitAsync(WaitTimeout, TestToken);
    }

    [Fact]
    public async Task ReentrantActor_TurnsNeverRunInParallel() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var probe = actors.Get<IParallelProbe>("p");

        // 32 concurrent calls, each yielding 20 times: plenty of opportunity for turns to
        // interleave — but the synchronous segments between awaits must never overlap. This is the
        // single-threaded guarantee reentrancy keeps.
        var calls = Enumerable.Range(0, 32).Select(_ => probe.Churn(TestToken).AsTask()).ToArray();
        await Task.WhenAll(calls).WaitAsync(WaitTimeout, TestToken);

        (await probe.Violations(TestToken)).Should().Be(0);
    }

    [Fact]
    public async Task ReentrantActor_StateCanChangeAcrossAwait() {
        await using var provider = CreateProvider();
        var actors = provider.GetRequiredService<IActorSystem>();
        var gate = provider.GetRequiredService<Gate>();
        var state = actors.Get<IReentrantState>("s");

        // The documented tradeoff of [Reentrant]: an interleaved message mutates state while
        // another message is parked on an await, and the parked message observes the new value.
        var read = state.ReadModeAfterAwait(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);

        await state.SetMode("changed", TestToken).AsTask().WaitAsync(WaitTimeout, TestToken);
        gate.Release();

        (await read.WaitAsync(WaitTimeout, TestToken)).Should().Be("changed");
    }

    private static ServiceProvider CreateProvider(FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        services.AddSingleton<Gate>();
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<SelfCallerActor, string, IReentrantSelfCaller> {
            Name = "ReentrantSelfCaller",
            Options = new ActorOptions { Reentrant = true },
            Activator = static (sp, context) => new SelfCallerActor(
                context,
                key => sp.GetRequiredService<IActorSystem>().Get<IReentrantSelfCaller>(key),
                sp.GetRequiredService<Gate>()),
            Facade = static handle => new ReentrantSelfCallerFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<SelfCallerActor, string, IBlockingSelfCaller> {
            Name = "BlockingSelfCaller",
            Options = new ActorOptions(),
            Activator = static (sp, context) => new SelfCallerActor(
                context,
                key => sp.GetRequiredService<IActorSystem>().Get<IBlockingSelfCaller>(key),
                sp.GetRequiredService<Gate>()),
            Facade = static handle => new BlockingSelfCallerFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<PingPongActor, string, IPingPong> {
            Name = "PingPong",
            Options = new ActorOptions { Reentrant = true },
            Activator = static (sp, context) => new PingPongActor(
                key => sp.GetRequiredService<IActorSystem>().Get<IPingPong>(key)),
            Facade = static handle => new PingPongFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<ParallelProbeActor, string, IParallelProbe> {
            Name = "ParallelProbe",
            Options = new ActorOptions { Reentrant = true },
            Activator = static (_, _) => new ParallelProbeActor(),
            Facade = static handle => new ParallelProbeFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<ReentrantStateActor, string, IReentrantState> {
            Name = "ReentrantState",
            Options = new ActorOptions { Reentrant = true },
            Activator = static (sp, _) => new ReentrantStateActor(sp.GetRequiredService<Gate>()),
            Facade = static handle => new ReentrantStateFacade(handle)
        });
        return services.BuildServiceProvider();
    }

    public sealed class Gate {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Opened => _gate.Task;

        public void Release() => _gate.TrySetResult();
    }

    // --- Self-caller: the same actor class registered reentrant and non-reentrant ---

    public interface ISelfNoop {
        ValueTask Noop(CancellationToken cancellationToken = default);
    }

    public interface IReentrantSelfCaller : IActorFacade<string>, ISelfNoop {
        ValueTask CallSelf(CancellationToken cancellationToken = default);
    }

    public interface IBlockingSelfCaller : IActorFacade<string>, ISelfNoop {
        ValueTask CallSelf(CancellationToken cancellationToken = default);
    }

    public sealed class SelfCallerActor(IActorContext<string> context, Func<string, ISelfNoop> self, Gate gate) {
        public async Task CallSelf(CancellationToken cancellationToken) {
            gate.Started.TrySetResult();
            await self(context.Key).Noop(cancellationToken);
        }

        public Task Noop() => Task.CompletedTask;
    }

    private sealed class ReentrantSelfCallerFacade(ActorHandle<SelfCallerActor> handle) : IReentrantSelfCaller {
        public ValueTask CallSelf(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CallSelfItem(), cancellationToken);

        public ValueTask Noop(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new NoopItem(), cancellationToken);
    }

    private sealed class BlockingSelfCallerFacade(ActorHandle<SelfCallerActor> handle) : IBlockingSelfCaller {
        public ValueTask CallSelf(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CallSelfItem(), cancellationToken);

        public ValueTask Noop(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new NoopItem(), cancellationToken);
    }

    private sealed class CallSelfItem : ActorWorkItem<SelfCallerActor, Unit> {
        public override string MethodName => "CallSelf";

        protected override async ValueTask<Unit> InvokeAsync(SelfCallerActor actor, CancellationToken cancellationToken) {
            await actor.CallSelf(cancellationToken).ConfigureAwait(false);
            return Unit.Value;
        }
    }

    private sealed class NoopItem : ActorWorkItem<SelfCallerActor, Unit> {
        public override string MethodName => "Noop";

        protected override async ValueTask<Unit> InvokeAsync(SelfCallerActor actor, CancellationToken cancellationToken) {
            await actor.Noop().ConfigureAwait(false);
            return Unit.Value;
        }
    }

    // --- Ping-pong cycle ---

    public interface IPingPong : IActorFacade<string> {
        ValueTask CallOther(string otherKey, CancellationToken cancellationToken = default);
        ValueTask Ping(CancellationToken cancellationToken = default);
    }

    public sealed class PingPongActor(Func<string, IPingPong> other) {
        public async Task CallOther(string otherKey, CancellationToken cancellationToken) =>
            await other(otherKey).Ping(cancellationToken);

        public Task Ping() => Task.CompletedTask;
    }

    private sealed class PingPongFacade(ActorHandle<PingPongActor> handle) : IPingPong {
        public ValueTask CallOther(string otherKey, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new CallOtherItem(otherKey), cancellationToken);

        public ValueTask Ping(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new PingItem(), cancellationToken);

        private sealed class CallOtherItem(string otherKey) : ActorWorkItem<PingPongActor, Unit> {
            public override string MethodName => "CallOther";

            protected override async ValueTask<Unit> InvokeAsync(PingPongActor actor, CancellationToken cancellationToken) {
                await actor.CallOther(otherKey, cancellationToken).ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class PingItem : ActorWorkItem<PingPongActor, Unit> {
            public override string MethodName => "Ping";

            protected override async ValueTask<Unit> InvokeAsync(PingPongActor actor, CancellationToken cancellationToken) {
                await actor.Ping().ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }

    // --- Parallelism probe: sync segments between awaits must never overlap ---

    public interface IParallelProbe : IActorFacade<string> {
        ValueTask Churn(CancellationToken cancellationToken = default);
        ValueTask<int> Violations(CancellationToken cancellationToken = default);
    }

    public sealed class ParallelProbeActor {
        private int _active;
        private int _violations;

        public async Task Churn() {
            for (var i = 0; i < 20; i++) {
                var entered = Interlocked.Increment(ref _active);
                Thread.SpinWait(200);   // widen the window so genuine overlap cannot slip through
                if (entered > 1 || Volatile.Read(ref _active) > 1) {
                    Interlocked.Increment(ref _violations);
                }

                Interlocked.Decrement(ref _active);
                await Task.Yield();
            }
        }

        public Task<int> Violations() => Task.FromResult(_violations);
    }

    private sealed class ParallelProbeFacade(ActorHandle<ParallelProbeActor> handle) : IParallelProbe {
        public ValueTask Churn(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ChurnItem(), cancellationToken);

        public ValueTask<int> Violations(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ViolationsItem(), cancellationToken);

        private sealed class ChurnItem : ActorWorkItem<ParallelProbeActor, Unit> {
            public override string MethodName => "Churn";

            protected override async ValueTask<Unit> InvokeAsync(ParallelProbeActor actor, CancellationToken cancellationToken) {
                await actor.Churn().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class ViolationsItem : ActorWorkItem<ParallelProbeActor, int> {
            public override string MethodName => "Violations";

            protected override async ValueTask<int> InvokeAsync(ParallelProbeActor actor, CancellationToken cancellationToken) =>
                await actor.Violations().ConfigureAwait(false);
        }
    }

    // --- State-across-await probe ---

    public interface IReentrantState : IActorFacade<string> {
        ValueTask<string> ReadModeAfterAwait(CancellationToken cancellationToken = default);
        ValueTask SetMode(string mode, CancellationToken cancellationToken = default);
    }

    public sealed class ReentrantStateActor(Gate gate) {
        private string _mode = "initial";

        public async Task<string> ReadModeAfterAwait() {
            gate.Started.TrySetResult();
            await gate.Opened;
            return _mode;
        }

        public Task SetMode(string mode) {
            _mode = mode;
            return Task.CompletedTask;
        }
    }

    private sealed class ReentrantStateFacade(ActorHandle<ReentrantStateActor> handle) : IReentrantState {
        public ValueTask<string> ReadModeAfterAwait(CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new ReadItem(), cancellationToken);

        public ValueTask SetMode(string mode, CancellationToken cancellationToken = default) =>
            handle.InvokeAsync(new SetModeItem(mode), cancellationToken);

        private sealed class ReadItem : ActorWorkItem<ReentrantStateActor, string> {
            public override string MethodName => "ReadModeAfterAwait";

            protected override async ValueTask<string> InvokeAsync(ReentrantStateActor actor, CancellationToken cancellationToken) =>
                await actor.ReadModeAfterAwait().ConfigureAwait(false);
        }

        private sealed class SetModeItem(string mode) : ActorWorkItem<ReentrantStateActor, Unit> {
            public override string MethodName => "SetMode";

            protected override async ValueTask<Unit> InvokeAsync(ReentrantStateActor actor, CancellationToken cancellationToken) {
                await actor.SetMode(mode).ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }
}
