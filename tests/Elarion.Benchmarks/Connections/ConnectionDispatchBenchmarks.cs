using System.Security.Claims;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Elarion.Connections.Simulation;
using Elarion.Identity;
using Elarion.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Benchmarks.Connections;

/// <summary>
/// The requirement-0 measurement baseline for low-allocation connection dispatch: allocated bytes/op and
/// time/op of <see cref="ConnectionHandlerInvoker.InvokeAsync{TRequest,TResponse}(TRequest,CancellationToken)"/>
/// on the typed, self-typed-marker path — the per-packet hot path of a game-server-like connection transport.
/// Every low-alloc mechanism (per-connection scope, singleton handlers, telemetry opt-down, pooled builders)
/// lands as an arm here, so the numbers — not intuition — decide each mechanism's value.
///
/// The chain is hand-wired exactly the way the handler registration generator emits it (concrete handler
/// scoped, <c>IHandler&lt;,&gt;</c> a scoped factory building the decorator chain per resolution, the merged
/// observability decorator outermost — the <see cref="Handlers.HandlerPipelineBenchmarks"/> philosophy), and
/// the realistic per-dispatch cost is included: the current-user dispatch-scope initializer is registered, so
/// every per-message scope pays context seeding like a production connection does.
///
/// The matrix:
/// <list type="bullet">
/// <item><c>PerMessageScope</c> — today's default: fresh seeded scope + chain re-resolution per message. The
/// baseline every opt-in mode is measured against, with <c>[Params]</c> 0 or 3 stateless custom decorators.</item>
/// <item><c>StructRequestMarkerOverload</c> vs <c>StructRequestExplicitGenerics</c> — the requirement-6 boxing
/// delta: the inferred marker overload takes the request as an interface, boxing a struct request per call;
/// the explicit-generic overload dispatches it unboxed.</item>
/// </list>
///
///   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*ConnectionDispatch*"
///
/// Read allocated bytes/op first: at thousands of messages per second per node, steady-state garbage — the
/// scope, the rebuilt decorator chain, the context dictionary — dominates GC pressure long before time/op
/// matters. Time/op distinguishes the resolution cost from the (near-free) invocation itself.
/// </summary>
[MemoryDiagnoser]
public class ConnectionDispatchBenchmarks {
    [Params(0, 3)] public int Decorators { get; set; }

    private ServiceProvider _provider = null!;
    private ConnectionHandlerInvoker _invoker = null!;
    private ConnectionHandlerInvoker _perConnectionInvoker = null!;
    private readonly EchoRequest _request = new(7);
    private readonly LowAllocEchoRequest _lowAllocRequest = new(7);
    private readonly StructEchoRequest _structRequest = new(7);

    private static CancellationToken Ct => CancellationToken.None;

    [GlobalSetup]
    public void Setup() {
        var services = new ServiceCollection();

        // The generator's emitted shape: concrete handler scoped, IHandler<,> a scoped factory composing the
        // chain per resolution — observability outermost, custom stateless decorators inside it.
        services.AddScoped<EchoHandler>();
        services.AddScoped<IHandler<EchoRequest, Result<int>>>(BuildEchoPipeline);
        // The full low-alloc profile (ADR-0066): [Handler(Scope = Singleton)] + [HandlerTelemetry(None)] —
        // singleton concrete, singleton chain with NO observability decorator, exactly what the generator
        // emits for that attribute combination.
        services.AddSingleton<LowAllocEchoHandler>();
        services.AddSingleton<IHandler<LowAllocEchoRequest, Result<int>>>(
            static sp => sp.GetRequiredService<LowAllocEchoHandler>());

        services.AddScoped<StructEchoHandler>();
        services.AddScoped<IHandler<StructEchoRequest, Result<int>>>(static sp => {
            IHandler<StructEchoRequest, Result<int>> handler = sp.GetRequiredService<StructEchoHandler>();
            var metadata = new HandlerMetadata(
                typeof(StructEchoHandler), typeof(StructEchoRequest), typeof(Result<int>));
            return new ObservabilityDecorator<StructEchoRequest, Result<int>>(
                handler,
                "bench.structEcho",
                metadata,
                sp.GetServices<IHandlerContextEnricher>(),
                sp.GetService<ILoggerFactory>());
        });
        // Realistic dispatch-scope seeding: the current-user initializer runs in every per-message scope.
        services.AddElarionClaimsCurrentUser();

        _provider = services.BuildServiceProvider();
        // An authenticated principal with a subject claim: the on-by-default user-context enricher reads the
        // user id for authenticated callers, exactly like a production connection dispatch.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "bench-user")], "test"));
        _invoker = new ConnectionHandlerInvoker(
            _provider, new SimulatedClientConnection("bench-user", principal: principal));
        _perConnectionInvoker = new ConnectionHandlerInvoker(
            _provider,
            new SimulatedClientConnection("bench-user", principal: principal),
            new ConnectionHandlerInvokerOptions { ScopeMode = ConnectionDispatchScopeMode.PerConnection });
    }

    private IHandler<EchoRequest, Result<int>> BuildEchoPipeline(IServiceProvider sp) {
        IHandler<EchoRequest, Result<int>> handler = sp.GetRequiredService<EchoHandler>();
        for (var i = 0; i < Decorators; i++)
            handler = new PassThroughDecorator(handler);

        var metadata = new HandlerMetadata(typeof(EchoHandler), typeof(EchoRequest), typeof(Result<int>));
        return new ObservabilityDecorator<EchoRequest, Result<int>>(
            handler,
            "bench.echo",
            metadata,
            sp.GetServices<IHandlerContextEnricher>(),
            sp.GetService<ILoggerFactory>());
    }

    [GlobalCleanup]
    public void Cleanup() {
        _perConnectionInvoker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _provider.Dispose();
    }

    // Every arm awaits the invoker and returns the unwrapped value so the async shape is identical across
    // arms and the trivial handler cannot be optimized away.

    [Benchmark(Baseline = true)]
    public async ValueTask<int> PerMessageScope() {
        var result = await _invoker.InvokeAsync(_request, Ct);
        return result.Value;
    }

    /// <summary>Requirements 1+2: reused connection scope, cached chain, reusable context, re-seed per message.</summary>
    [Benchmark]
    public async ValueTask<int> PerConnectionScope() {
        var result = await _perConnectionInvoker.InvokeAsync(_request, Ct);
        return result.Value;
    }

    /// <summary>The full low-alloc profile: per-connection scope + singleton handler + telemetry None.</summary>
    [Benchmark]
    public async ValueTask<int> LowAllocProfile() {
        var result = await _perConnectionInvoker.InvokeAsync(_lowAllocRequest, Ct);
        return result.Value;
    }

    /// <summary>The self-typed-marker overload boxes the struct request (interface-typed parameter).</summary>
    [Benchmark]
    public async ValueTask<int> StructRequestMarkerOverload() {
        var result = await _invoker.InvokeAsync(_structRequest, Ct);
        return result.Value;
    }

    /// <summary>The explicit-generic overload dispatches the struct request unboxed.</summary>
    [Benchmark]
    public async ValueTask<int> StructRequestExplicitGenerics() {
        var result = await _invoker.InvokeAsync<StructEchoRequest, int>(_structRequest, Ct);
        return result.Value;
    }

    // --- The handlers under test: bare [Handler] equivalents (manual wiring mirrors the generator). ---

    public sealed record EchoRequest(int Value) : IQuery<EchoRequest, int>;

    public sealed record LowAllocEchoRequest(int Value) : IQuery<LowAllocEchoRequest, int>;

    public readonly record struct StructEchoRequest(int Value) : IQuery<StructEchoRequest, int>;

    public sealed class EchoHandler : IHandler<EchoRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(EchoRequest request, CancellationToken ct) {
            Result<int> result = request.Value + 1;
            return new ValueTask<Result<int>>(result);
        }
    }

    public sealed class LowAllocEchoHandler : IHandler<LowAllocEchoRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(LowAllocEchoRequest request, CancellationToken ct) {
            Result<int> result = request.Value + 1;
            return new ValueTask<Result<int>>(result);
        }
    }

    public sealed class StructEchoHandler : IHandler<StructEchoRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(StructEchoRequest request, CancellationToken ct) {
            Result<int> result = request.Value + 1;
            return new ValueTask<Result<int>>(result);
        }
    }

    private sealed class PassThroughDecorator(IHandler<EchoRequest, Result<int>> inner)
        : IHandler<EchoRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(EchoRequest request, CancellationToken ct) {
            return inner.HandleAsync(request, ct);
        }
    }
}
