using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// Bounded-mailbox behaviour: the call timeout bounds a Wait-mode enqueue into a full mailbox
/// (the deadlock backstop covers queue admission, not just execution), Fail mode surfaces
/// <see cref="ActorMailboxFullException"/>, and an abandoned enqueue never corrupts the activation.
/// </summary>
public sealed class ActorMailboxTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BoundedWaitMailbox_FullBehindABlockedTurn_CallTimeoutBoundsTheEnqueue() {
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider(ActorMailboxFullMode.Wait, time);
        var gate = provider.GetRequiredService<GateService>();
        var bounded = provider.GetRequiredService<IActorSystem>().Get<IBounded>("b");

        var running = bounded.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);
        // Fills the single mailbox slot behind the blocked turn ...
        var queued = bounded.Count(TestToken).AsTask();
        // ... so this call's Wait-mode enqueue blocks on the full mailbox.
        var stuck = bounded.Count(TestToken).AsTask();

        time.Advance(ActorOptions.DefaultCallTimeout + TimeSpan.FromSeconds(1));

        // The enqueue wait is bounded by the invocation timeout and surfaces as the same backstop
        // TimeoutException an executing call produces — never an indefinite hang.
        var act = async () => await stuck.WaitAsync(WaitTimeout, TestToken);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*deadlock backstop*");

        gate.Release();
        // The blocked turn's caller and the queued call also hit the backstop while the clock jumped.
        foreach (var timedOut in new[] { running, queued }) {
            var timedOutAct = async () => await timedOut.WaitAsync(WaitTimeout, TestToken);
            await timedOutAct.Should().ThrowAsync<TimeoutException>();
        }

        // The cancelled enqueue never landed in the mailbox and the timed-out queued call was
        // skipped: nothing counted, and the same activation keeps serving new calls.
        (await bounded.Executions(TestToken).AsTask().WaitAsync(WaitTimeout, TestToken)).Should().Be(0);
        await bounded.Count(TestToken).AsTask().WaitAsync(WaitTimeout, TestToken);
        (await bounded.Executions(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task BoundedFailMailbox_Full_ThrowsActorMailboxFullException() {
        await using var provider = CreateProvider(ActorMailboxFullMode.Fail);
        var gate = provider.GetRequiredService<GateService>();
        var bounded = provider.GetRequiredService<IActorSystem>().Get<IBounded>("b");

        var running = bounded.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);
        var queued = bounded.Count(TestToken).AsTask();

        var act = async () => await bounded.Count(TestToken);
        await act.Should().ThrowAsync<ActorMailboxFullException>().WithMessage("*capacity 1*");

        gate.Release();
        await Task.WhenAll(running, queued).WaitAsync(WaitTimeout, TestToken);
        // The rejected call never executed; the queued one did.
        (await bounded.Executions(TestToken)).Should().Be(1);
    }

    [Fact]
    public async Task CancelledWaitEnqueue_NeverLandsInTheMailbox_ActorKeepsWorking() {
        await using var provider = CreateProvider(ActorMailboxFullMode.Wait);
        var gate = provider.GetRequiredService<GateService>();
        var bounded = provider.GetRequiredService<IActorSystem>().Get<IBounded>("b");

        var running = bounded.WaitForGate(TestToken).AsTask();
        await gate.Started.Task.WaitAsync(WaitTimeout, TestToken);
        var queued = bounded.Count(TestToken).AsTask();

        using var cts = new CancellationTokenSource();
        var stuck = bounded.Count(cts.Token).AsTask();
        cts.Cancel();

        var act = async () => await stuck.WaitAsync(WaitTimeout, TestToken);
        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.Release();
        await Task.WhenAll(running, queued).WaitAsync(WaitTimeout, TestToken);
        // Only the queued call ran; the cancelled write never landed, and the mailbox stays healthy.
        (await bounded.Executions(TestToken)).Should().Be(1);
        await bounded.Count(TestToken).AsTask().WaitAsync(WaitTimeout, TestToken);
        (await bounded.Executions(TestToken)).Should().Be(2);
    }

    private static ServiceProvider CreateProvider(
        ActorMailboxFullMode fullMode, FakeTimeProvider? timeProvider = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        services.AddSingleton<GateService>();
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<BoundedActor, string, IBounded> {
            Name = "Bounded",
            Options = new ActorOptions { MailboxCapacity = 1, MailboxFullMode = fullMode },
            Activator = static (sp, _) => new BoundedActor(sp.GetRequiredService<GateService>()),
            Facade = static handle => new BoundedFacade(handle)
        });
        return services.BuildServiceProvider();
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

    public interface IBounded : IActorFacade<string> {
        ValueTask WaitForGate(CancellationToken cancellationToken = default);
        ValueTask Count(CancellationToken cancellationToken = default);
        ValueTask<int> Executions(CancellationToken cancellationToken = default);
    }

    public sealed class BoundedActor(GateService gate) {
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

    private sealed class BoundedFacade(ActorHandle<BoundedActor> handle) : IBounded {
        public ValueTask WaitForGate(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new WaitForGateItem(), cancellationToken);
        }

        public ValueTask Count(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new CountItem(), cancellationToken);
        }

        public ValueTask<int> Executions(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new ExecutionsItem(), cancellationToken);
        }

        private sealed class WaitForGateItem : ActorWorkItem<BoundedActor, Unit> {
            public override string MethodName => "WaitForGate";

            protected override async ValueTask<Unit> InvokeAsync(BoundedActor actor,
                CancellationToken cancellationToken) {
                await actor.WaitForGate().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class CountItem : ActorWorkItem<BoundedActor, Unit> {
            public override string MethodName => "Count";

            protected override async ValueTask<Unit> InvokeAsync(BoundedActor actor,
                CancellationToken cancellationToken) {
                await actor.Count().ConfigureAwait(false);
                return Unit.Value;
            }
        }

        private sealed class ExecutionsItem : ActorWorkItem<BoundedActor, int> {
            public override string MethodName => "Executions";

            protected override async ValueTask<int>
                InvokeAsync(BoundedActor actor, CancellationToken cancellationToken) {
                return await actor.Executions().ConfigureAwait(false);
            }
        }
    }
}
