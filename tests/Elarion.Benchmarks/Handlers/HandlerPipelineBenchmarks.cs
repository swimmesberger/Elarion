using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using Elarion.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Benchmarks.Handlers;

/// <summary>
/// Baseline numbers for the default handler dispatch pipeline — the hot path every request takes —
/// gating the "near-zero-cost abstractions" claim. A handler carrying only <c>[Handler]</c> (no gate
/// attributes) is wrapped by exactly one always-on decorator: <see cref="ObservabilityDecorator{TRequest,TResponse}"/>
/// (the merged tracing + context enrichment, ADR-0059), the outermost decorator. This benchmark
/// hand-wires that exact chain the way the registration generator emits it (concrete handler + a
/// scoped decorator factory), so it measures the real runtime decorator type
/// without the module-bootstrapper machinery — the ActorCallBenchmarks philosophy.
///
/// The matrix (each arm runs with and without an OTel listener on the <c>Elarion.Handlers</c> source,
/// via <see cref="TracingListener"/> — the whole point of tracing being "free when nothing listens"):
/// <list type="bullet">
/// <item><c>DirectCall</c> — the bare handler's <c>HandleAsync</c> (no DI, no decorators): the floor.</item>
/// <item><c>Decorated</c> — a cached decorated chain: the per-call cost of the always-on observability decorator.</item>
/// <item><c>ScopeAndBareHandler</c> — a fresh scope + resolve the bare concrete handler + call: the DI
/// scope + scoped-instance cost a minimal API pays for <em>any</em> handler, decorators or not.</item>
/// <item><c>ResolveScopedAndCall</c> — a fresh DI scope, resolve <c>IHandler&lt;,&gt;</c> (builds a fresh chain), call: the realistic per-request cost.</item>
/// <item><c>ViaHandlerSender</c> — the same through the typed <see cref="IHandlerSender"/> mediator.</item>
/// </list>
/// The per-request allocation splits cleanly:
/// <c>ResolveScopedAndCall − ScopeAndBareHandler</c> is the cost Elarion actually adds over a bare
/// handler (the single always-on decorator object rebuilt per resolution). The rest —
/// <see cref="System.IServiceProvider"/> scope creation and the scoped handler instance — is paid by
/// the framework's request scope regardless of Elarion.
///
///   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*HandlerPipeline*"
///
/// Read the ABSOLUTE numbers, not the ratios: the baseline is a near-no-op awaited call (~4 ns), so
/// the ratio column is large by construction. The point is that <c>Decorated</c> with no listener
/// allocates <b>zero bytes</b> and adds only tens of nanoseconds — noise beside any real handler body
/// (a DB round trip is microseconds). Tracing is free until observed: attaching a listener is the only
/// thing that makes the decorated chain allocate (an <c>Activity</c> per call).
/// </summary>
[MemoryDiagnoser]
public class HandlerPipelineBenchmarks {
    [Params(false, true)]
    public bool TracingListener { get; set; }

    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private EchoHandler _direct = null!;
    private IHandler<EchoRequest, Result<int>> _decorated = null!;
    private ActivityListener? _listener;
    private readonly EchoRequest _request = new(7);

    private static CancellationToken Ct => CancellationToken.None;

    [GlobalSetup]
    public void Setup() {
        if (TracingListener) {
            // A real listener on the handler source: the observability decorator now starts an Activity per
            // call (its HasListeners() fast path is off), so these rows show the active-tracing cost.
            _listener = new ActivityListener {
                ShouldListenTo = source => source.Name == HandlerTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        var services = new ServiceCollection();

        // Exactly the shape the handler registration generator emits for a bare [Handler]: the concrete
        // handler scoped, and IHandler<TReq, TResp> a scoped factory that builds the decorator chain per
        // resolution — one always-on observability decorator (merged tracing + context enrichment), outermost.
        services.AddScoped<EchoHandler>();
        services.AddScoped<IHandler<EchoRequest, Result<int>>>(static sp => {
            IHandler<EchoRequest, Result<int>> handler = sp.GetRequiredService<EchoHandler>();
            var metadata = new HandlerMetadata(typeof(EchoHandler), typeof(EchoRequest), typeof(Result<int>));
            handler = new ObservabilityDecorator<EchoRequest, Result<int>>(
                handler,
                "bench.echo",
                metadata,
                sp.GetServices<IHandlerContextEnricher>(),
                sp.GetService<ILoggerFactory>());
            return handler;
        });
        services.AddElarionHandlerSender();

        _provider = services.BuildServiceProvider();
        _direct = new EchoHandler();
        // A long-lived scope holds the cached decorated chain for the per-call-cost arm.
        _scope = _provider.CreateScope();
        _decorated = _scope.ServiceProvider.GetRequiredService<IHandler<EchoRequest, Result<int>>>();
    }

    [GlobalCleanup]
    public void Cleanup() {
        _scope.Dispose();
        _provider.Dispose();
        _listener?.Dispose();
    }

    // Every arm awaits the handler and returns the unwrapped value: an identical async shape across
    // arms (all await one synchronously-completed ValueTask), so the delta over the baseline is purely
    // the decorators / DI resolution — and the returned int keeps the trivial handler from being
    // optimized to nothing.

    [Benchmark(Baseline = true)]
    public async ValueTask<int> DirectCall() {
        var result = await _direct.HandleAsync(_request, Ct);
        return result.Value;
    }

    [Benchmark]
    public async ValueTask<int> Decorated() {
        var result = await _decorated.HandleAsync(_request, Ct);
        return result.Value;
    }

    // A fresh scope + resolve the BARE concrete handler (no decorators) + call. In a real minimal API
    // the request scope already exists, so subtracting this from ResolveScopedAndCall isolates the
    // per-request cost of the two always-on decorators specifically.
    [Benchmark]
    public async ValueTask<int> ScopeAndBareHandler() {
        using var scope = _provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<EchoHandler>();
        var result = await handler.HandleAsync(_request, Ct);
        return result.Value;
    }

    [Benchmark]
    public async ValueTask<int> ResolveScopedAndCall() {
        using var scope = _provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IHandler<EchoRequest, Result<int>>>();
        var result = await handler.HandleAsync(_request, Ct);
        return result.Value;
    }

    [Benchmark]
    public async ValueTask<int> ViaHandlerSender() {
        using var scope = _provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IHandlerSender>();
        var result = await sender.SendAsync<EchoRequest, int>(_request, Ct);
        return result.Value;
    }

    // --- The handler under test: a bare [Handler] equivalent (manual wiring mirrors the generator). ---

    public sealed record EchoRequest(int Value);

    public sealed class EchoHandler : IHandler<EchoRequest, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(EchoRequest request, CancellationToken ct) {
            Result<int> result = request.Value + 1;
            return new ValueTask<Result<int>>(result);
        }
    }
}
