using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Actors;

/// <summary>
/// Activation replacement is serialized on the predecessor's drain (Orleans-style): when a cell
/// closes, the replacement for the same key must not construct/load/<c>OnActivateAsync</c> until
/// the old activation's <c>OnDeactivateAsync</c> and DI-scope disposal fully completed — bounded
/// by <see cref="ActorOptions.DeactivationTimeout"/> so a hung deactivation degrades to a warned
/// overlap instead of bricking the key forever.
/// </summary>
public sealed class ActorActivationReplacementTests {
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReplacementActivation_WaitsUntilPredecessorFinishesDeactivating() {
        var time = new FakeTimeProvider();
        var observed = new ReplacementObservations();
        await using var provider = CreateLingeringProvider(observed, time);
        var probe = provider.GetRequiredService<IActorSystem>().Get<ILingering>("a");

        await probe.Ping(TestToken);
        observed.Activations.Should().Be(1);

        // Drive idle passivation into the gated OnDeactivateAsync: the old activation is now
        // mid-deactivation and stays there until the gate releases.
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(1), () => observed.DeactivateEntered.Task.IsCompleted);

        // A new call arrives while the predecessor is still deactivating. Its replacement
        // activation must not begin — no OnActivateAsync, no result — until the predecessor's
        // lifecycle completes.
        var pending = probe.Ping(TestToken).AsTask();
        await Task.Delay(200, TestToken);
        observed.Activations.Should().Be(1, "the replacement must not activate while the predecessor deactivates");
        pending.IsCompleted.Should().BeFalse();

        observed.DeactivateGate.TrySetResult();
        await pending.WaitAsync(WaitTimeout, TestToken);
        observed.Activations.Should().Be(2);
        observed.DeactivationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task HungPredecessorDeactivation_TimesOut_AndTheReplacementProceeds() {
        var time = new FakeTimeProvider();
        var observed = new ReplacementObservations();
        await using var provider = CreateLingeringProvider(
            observed, time, TimeSpan.FromSeconds(5));
        var probe = provider.GetRequiredService<IActorSystem>().Get<ILingering>("a");

        await probe.Ping(TestToken);
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(1), () => observed.DeactivateEntered.Task.IsCompleted);

        // The predecessor's OnDeactivateAsync hangs (the gate is never released). Once the
        // DeactivationTimeout elapses, the replacement proceeds with a warning — the timeout is
        // what keeps a hung deactivation from bricking the key forever.
        var pending = probe.Ping(TestToken).AsTask();
        await AdvanceUntilAsync(time, TimeSpan.FromSeconds(1), () => pending.IsCompleted);

        await pending;
        observed.Activations.Should().Be(2);
        observed.DeactivationsCompleted.Should().Be(0, "the predecessor is still hung — the replacement overtook it");

        // Unblock the hung deactivation so its loop can finish.
        observed.DeactivateGate.TrySetResult();
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task ExclusiveResource_IsNeverDoubleHeld_AcrossRapidPassivationReactivation() {
        // The flagship "actor owns an exclusive external resource" use case: acquired in
        // OnActivateAsync, released in OnDeactivateAsync. A tiny idle timeout makes the cell
        // passivate in every gap between call bursts, so replacements constantly race the old
        // activation's drain — without replacement serialization the new acquire can precede the
        // old release (a brief double-hold).
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        var resource = new ExclusiveResource();
        services.AddSingleton(resource);
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<ExclusiveActor, string, IExclusive> {
            Name = "Exclusive",
            Options = new ActorOptions { IdleTimeout = TimeSpan.FromMilliseconds(1) },
            Activator = static (sp, _) => new ExclusiveActor(sp.GetRequiredService<ExclusiveResource>()),
            Facade = static handle => new ExclusiveFacade(handle)
        });
        await using var provider = services.BuildServiceProvider();
        var exclusive = provider.GetRequiredService<IActorSystem>().Get<IExclusive>("hot");

        const int rounds = 150;
        const int batch = 8;
        for (var round = 0; round < rounds; round++) {
            var calls = Enumerable.Range(0, batch)
                .Select(_ => exclusive.Touch(TestToken).AsTask())
                .ToArray();
            await Task.WhenAll(calls).WaitAsync(WaitTimeout, TestToken);
            // Let the 1 ms idle timer fire in the gap so the next burst races a fresh passivation.
            await Task.Delay(2, TestToken);
        }

        resource.DoubleHolds.Should().Be(0, "an exclusive resource must never be acquired while still held");
        // The run only proves the race window was exercised if passivation actually happened.
        resource.Releases.Should().BeGreaterThan(0);
    }

    private static ServiceProvider CreateLingeringProvider(
        ReplacementObservations observations, FakeTimeProvider time, TimeSpan? deactivationTimeout = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(observations);
        services.AddElarionActorSystem();
        services.AddElarionActor(new ActorRegistration<LingeringActor, string, ILingering> {
            Name = "Lingering",
            Options = new ActorOptions {
                // The fake clock advances freely while calls are pending; the call timeout must
                // not race the deactivation-timeout mechanics under test.
                CallTimeout = null,
                DeactivationTimeout = deactivationTimeout ?? ActorOptions.DefaultDeactivationTimeout
            },
            Activator = static (sp, _) => new LingeringActor(sp.GetRequiredService<ReplacementObservations>()),
            Facade = static handle => new LingeringFacade(handle)
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

    public sealed class ReplacementObservations {
        private int _activations;
        private int _deactivationsCompleted;

        public int Activations => Volatile.Read(ref _activations);
        public int DeactivationsCompleted => Volatile.Read(ref _deactivationsCompleted);

        public TaskCompletionSource DeactivateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DeactivateGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordActivation() {
            Interlocked.Increment(ref _activations);
        }

        public void RecordDeactivationCompleted() {
            Interlocked.Increment(ref _deactivationsCompleted);
        }
    }

    // --- Lingering (a gated OnDeactivateAsync so tests control the drain deterministically) ---

    public interface ILingering : IActorFacade<string> {
        ValueTask Ping(CancellationToken cancellationToken = default);
    }

    public sealed class LingeringActor(ReplacementObservations observations) : IActorLifecycle {
        public ValueTask OnActivateAsync(CancellationToken cancellationToken) {
            observations.RecordActivation();
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
            observations.DeactivateEntered.TrySetResult();
            await observations.DeactivateGate.Task.ConfigureAwait(false);
            observations.RecordDeactivationCompleted();
        }

        public Task Ping() {
            return Task.CompletedTask;
        }
    }

    private sealed class LingeringFacade(ActorHandle<LingeringActor> handle) : ILingering {
        public ValueTask Ping(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new PingItem(), cancellationToken);
        }

        private sealed class PingItem : ActorWorkItem<LingeringActor, Unit> {
            public override string MethodName => "Ping";

            protected override async ValueTask<Unit> InvokeAsync(LingeringActor actor,
                CancellationToken cancellationToken) {
                await actor.Ping().ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }

    // --- Exclusive (acquire on activate, release on deactivate — the double-hold probe) ---

    public sealed class ExclusiveResource {
        private int _holders;
        private int _doubleHolds;
        private int _releases;

        public int DoubleHolds => Volatile.Read(ref _doubleHolds);
        public int Releases => Volatile.Read(ref _releases);

        public void Acquire() {
            if (Interlocked.Increment(ref _holders) != 1) Interlocked.Increment(ref _doubleHolds);
        }

        public void Release() {
            Interlocked.Decrement(ref _holders);
            Interlocked.Increment(ref _releases);
        }
    }

    public interface IExclusive : IActorFacade<string> {
        ValueTask Touch(CancellationToken cancellationToken = default);
    }

    public sealed class ExclusiveActor(ExclusiveResource resource) : IActorLifecycle {
        public ValueTask OnActivateAsync(CancellationToken cancellationToken) {
            resource.Acquire();
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnDeactivateAsync(CancellationToken cancellationToken) {
            // Widen the pre-fix race window: a replacement that does not wait for this lifecycle
            // would acquire while the release is still pending here.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            resource.Release();
        }

        public Task Touch() {
            return Task.CompletedTask;
        }
    }

    private sealed class ExclusiveFacade(ActorHandle<ExclusiveActor> handle) : IExclusive {
        public ValueTask Touch(CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new TouchItem(), cancellationToken);
        }

        private sealed class TouchItem : ActorWorkItem<ExclusiveActor, Unit> {
            public override string MethodName => "Touch";

            protected override async ValueTask<Unit> InvokeAsync(ExclusiveActor actor,
                CancellationToken cancellationToken) {
                await actor.Touch().ConfigureAwait(false);
                return Unit.Value;
            }
        }
    }
}
