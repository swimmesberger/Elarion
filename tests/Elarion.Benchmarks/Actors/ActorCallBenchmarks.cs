using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Elarion.Actors;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Benchmarks.Actors;

/// <summary>
/// Baseline numbers for the actor call path, gating the ADR-0042 optimization roadmap (facade
/// pass-through, sync-enqueue fast path, IValueTaskSource work items). The matrix:
/// <list type="bullet">
/// <item><c>DirectCall</c> — the plain awaited method call an actor call is compared against (the floor).</item>
/// <item><c>Ask</c> — one awaited facade call on a non-reentrant actor (the default configuration).</item>
/// <item><c>Ask_NoCallTimeout</c> — isolates the per-call timeout cost (CancellationTokenSource + timer).</item>
/// <item><c>Ask_Reentrant</c> — isolates the exclusive-scheduler cost only [Reentrant] actors pay.</item>
/// <item><c>Ask_Pipelined</c> — 1000 calls in flight against one mailbox (throughput, not latency).</item>
/// <item><c>PingPong</c> — sequential actor→actor round trips (the FeatherAct classic).</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
public class ActorCallBenchmarks {
    private const int PipelineDepth = 1000;
    private const int PingPongRounds = 1000;

    private ServiceProvider _provider = null!;
    private EchoActor _direct = null!;
    private IEcho _echo = null!;
    private IEchoNoTimeout _echoNoTimeout = null!;
    private IEchoReentrant _echoReentrant = null!;
    private IRelay _relay = null!;
    private Task<int>[] _pipeline = null!;

    [GlobalSetup]
    public void Setup() {
        var services = new ServiceCollection();
        services.AddElarionActorSystem();
        // IdleTimeout = null: no passivation churn between iterations; CallTimeout matches the
        // shipped default except in the variant that isolates its cost.
        services.AddElarionActor(new ActorRegistration<EchoActor, ActorSingletonKey, IEcho> {
            Name = "Echo",
            Options = new ActorOptions { IdleTimeout = null },
            Activator = static (_, _) => new EchoActor(),
            Facade = static handle => new EchoFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<EchoActor, ActorSingletonKey, IEchoNoTimeout> {
            Name = "EchoNoTimeout",
            Options = new ActorOptions { IdleTimeout = null, CallTimeout = null },
            Activator = static (_, _) => new EchoActor(),
            Facade = static handle => new EchoNoTimeoutFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<EchoActor, ActorSingletonKey, IEchoReentrant> {
            Name = "EchoReentrant",
            Options = new ActorOptions { IdleTimeout = null, Reentrant = true },
            Activator = static (_, _) => new EchoActor(),
            Facade = static handle => new EchoReentrantFacade(handle)
        });
        services.AddElarionActor(new ActorRegistration<RelayActor, ActorSingletonKey, IRelay> {
            Name = "Relay",
            Options = new ActorOptions { IdleTimeout = null, CallTimeout = null },
            Activator = static (sp, _) => new RelayActor(
                sp.GetRequiredService<IActorSystem>().Get<IEcho>()),
            Facade = static handle => new RelayFacade(handle)
        });

        _provider = services.BuildServiceProvider();
        var actors = _provider.GetRequiredService<IActorSystem>();
        _direct = new EchoActor();
        _echo = actors.Get<IEcho>();
        _echoNoTimeout = actors.Get<IEchoNoTimeout>();
        _echoReentrant = actors.Get<IEchoReentrant>();
        _relay = actors.Get<IRelay>();
        _pipeline = new Task<int>[PipelineDepth];
    }

    [GlobalCleanup]
    public void Cleanup() {
        _provider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<int> DirectCall() {
        return _direct.Echo(7);
    }

    [Benchmark]
    public ValueTask<int> Ask() {
        return _echo.Echo(7);
    }

    [Benchmark]
    public ValueTask<int> Ask_NoCallTimeout() {
        return _echoNoTimeout.Echo(7);
    }

    [Benchmark]
    public ValueTask<int> Ask_Reentrant() {
        return _echoReentrant.Echo(7);
    }

    [Benchmark(OperationsPerInvoke = PipelineDepth)]
    public async Task<int> Ask_Pipelined() {
        for (var i = 0; i < PipelineDepth; i++) _pipeline[i] = _echo.Echo(i).AsTask();

        await Task.WhenAll(_pipeline);
        return _pipeline[PipelineDepth - 1].Result;
    }

    [Benchmark(OperationsPerInvoke = PingPongRounds)]
    public ValueTask PingPong() {
        return _relay.Run(PingPongRounds);
    }

    // --- Actors under test ---

    public sealed class EchoActor {
        public Task<int> Echo(int value) {
            return Task.FromResult(value + 1);
        }
    }

    public sealed class RelayActor(IEcho echo) {
        public async Task Run(int rounds) {
            for (var i = 0; i < rounds; i++) await echo.Echo(i);
        }
    }

    // --- Hand-rolled facades (mirroring what the generator emits) ---

    public interface IEcho : IActorFacade {
        ValueTask<int> Echo(int value, CancellationToken cancellationToken = default);
    }

    public interface IEchoNoTimeout : IActorFacade {
        ValueTask<int> Echo(int value, CancellationToken cancellationToken = default);
    }

    public interface IEchoReentrant : IActorFacade {
        ValueTask<int> Echo(int value, CancellationToken cancellationToken = default);
    }

    public interface IRelay : IActorFacade {
        ValueTask Run(int rounds, CancellationToken cancellationToken = default);
    }

    private sealed class EchoFacade(ActorHandle<EchoActor> handle) : IEcho {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(EchoItem.Rent(value), cancellationToken);
        }
    }

    private sealed class EchoNoTimeoutFacade(ActorHandle<EchoActor> handle) : IEchoNoTimeout {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(EchoItem.Rent(value), cancellationToken);
        }
    }

    private sealed class EchoReentrantFacade(ActorHandle<EchoActor> handle) : IEchoReentrant {
        public ValueTask<int> Echo(int value, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(EchoItem.Rent(value), cancellationToken);
        }
    }

    // Prototype of a pooled work item (what the generator would emit): rented per call, returned to
    // the pool on the Recycle hook. The caller captures the completion Task before enqueue, so reuse
    // is safe. Measures the work-item-object allocation this pooling removes.
    private sealed class EchoItem : ActorWorkItem<EchoActor, int> {
        private static readonly ConcurrentQueue<EchoItem> Pool = new();
        private int _value;

        public override string MethodName => "Echo";

        public static EchoItem Rent(int value) {
            if (!Pool.TryDequeue(out var item)) item = new EchoItem();

            item._value = value;
            return item;
        }

        protected override void Recycle() {
            Pool.Enqueue(this);
        }

        protected override async ValueTask<int> InvokeAsync(EchoActor actor, CancellationToken cancellationToken) {
            return await actor.Echo(_value).ConfigureAwait(false);
        }
    }

    private sealed class RelayFacade(ActorHandle<RelayActor> handle) : IRelay {
        public ValueTask Run(int rounds, CancellationToken cancellationToken = default) {
            return handle.InvokeAsync(new RunItem(rounds), cancellationToken);
        }
    }

    private sealed class RunItem(int rounds) : ActorWorkItem<RelayActor, Abstractions.Results.Unit> {
        public override string MethodName => "Run";

        protected override async ValueTask<Abstractions.Results.Unit> InvokeAsync(RelayActor actor,
            CancellationToken cancellationToken) {
            await actor.Run(rounds).ConfigureAwait(false);
            return Abstractions.Results.Unit.Value;
        }
    }
}
