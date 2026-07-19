using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Pipeline;
using Elarion.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests;

public sealed class StreamHandlerInvokerTests {
    private sealed record Request;

    private sealed class Probe : IDisposable {
        private int _disposeCount;
        public bool Disposed { get; private set; }
        public int DisposeCount => _disposeCount;
        public void Dispose() { Disposed = true; Interlocked.Increment(ref _disposeCount); }
    }

    private sealed class Handler(Probe probe) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Values(probe)));

        private static async IAsyncEnumerable<int> Values(Probe probe) {
            yield return 1;
            await Task.Yield();
            probe.Disposed.Should().BeFalse();
            yield return 2;
        }
    }

    private sealed class FaultingHandler(Probe probe) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Fault(probe)));

        private static async IAsyncEnumerable<int> Fault(Probe probe) {
            await Task.Yield();
            _ = probe;
            throw new InvalidOperationException("expected");
#pragma warning disable CS0162
            yield return 0;
#pragma warning restore CS0162
        }
    }

    private sealed class CleanupHandler(Probe probe, bool fault) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Values(probe, fault)));

        private static async IAsyncEnumerable<int> Values(Probe probe, bool fault) {
            try {
                yield return 1;
                await Task.Yield();
                if (fault)
                    throw new InvalidOperationException("expected");
            } finally {
                // The enumerator's cleanup is still part of the stream invocation and may use a scoped service.
                probe.Disposed.Should().BeFalse();
            }
        }
    }

    private sealed class WaitingHandler(Probe probe) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Wait(probe)));

        private static async IAsyncEnumerable<int> Wait(Probe probe, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
            _ = probe;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }

    private sealed class Gate {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingHandler(Probe probe, Gate gate) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Wait(probe, gate)));

        private static async IAsyncEnumerable<int> Wait(Probe probe, Gate gate) {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
            probe.Disposed.Should().BeFalse();
            yield return 1;
        }
    }

    private sealed class RejectingHandler : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult<Result<IAsyncEnumerable<int>>>(AppError.NotFound("rejected"));
    }

    [Fact]
    public async Task InvokeAsync_KeepsScopeUntilEnumerationCompletes_ThenDisposesIt() {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, Handler>()
            .BuildServiceProvider();

        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        start.IsSuccess.Should().BeTrue();
        probe.Should().NotBeNull();
        probe!.Disposed.Should().BeFalse();

        var values = new List<int>();
        await foreach (var value in start.Value)
            values.Add(value);

        values.Should().Equal(1, 2);
        probe.Disposed.Should().BeTrue();
    }

    private sealed record InferredRequest : IStreamRequest<InferredRequest, int>;

    private sealed class InferredHandler : IStreamHandler<InferredRequest, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(InferredRequest request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Items()));

        private static async IAsyncEnumerable<int> Items() {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }
    }

    [Fact]
    public async Task InvokeAsync_SelfTypedStreamMarkerInfersBothTypeArguments() {
        using var provider = new ServiceCollection()
            .AddScoped<IStreamHandler<InferredRequest, int>, InferredHandler>()
            .BuildServiceProvider();

        Result<StreamHandlerInvocation<int>> start = await StreamHandlerInvoker.InvokeAsync(
            provider, new InferredRequest(), ct: TestContext.Current.CancellationToken);

        start.IsSuccess.Should().BeTrue();
        var values = new List<int>();
        await foreach (var value in start.Value!)
            values.Add(value);
        values.Should().Equal(1, 2);
    }

    [Fact]
    public async Task DisposeAsync_WithoutEnumeration_ReleasesScopeExactlyOnce() {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, Handler>()
            .BuildServiceProvider();

        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        await start.Value.DisposeAsync();
        await start.Value.DisposeAsync();

        probe!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentDisposeAsync_WaitsForTheSameScopeDisposal() {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, Handler>()
            .BuildServiceProvider();

        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        await Task.WhenAll(
            start.Value.DisposeAsync().AsTask(),
            start.Value.DisposeAsync().AsTask(),
            start.Value.DisposeAsync().AsTask());

        probe!.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task FaultedEnumeration_ReleasesScopeExactlyOnce() {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, FaultingHandler>()
            .BuildServiceProvider();

        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        var enumerate = async () => {
            await foreach (var _ in start.Value) { }
        };

        await enumerate.Should().ThrowAsync<InvalidOperationException>();
        probe!.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalEnumeration_DisposesInnerBeforeItsScope(bool fault) {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>>(sp => new CleanupHandler(sp.GetRequiredService<Probe>(), fault))
            .BuildServiceProvider();

        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        var enumerate = async () => {
            await foreach (var _ in start.Value) { }
        };

        if (fault)
            await enumerate.Should().ThrowAsync<InvalidOperationException>();
        else
            await enumerate();

        probe!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CancelledEnumeration_ReleasesScopeExactlyOnce() {
        Probe? probe = null;
        using var provider = new ServiceCollection()
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, WaitingHandler>()
            .BuildServiceProvider();
        using var cancelled = new CancellationTokenSource();
        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);

        var enumerator = start.Value.GetAsyncEnumerator(cancelled.Token);
        var pending = enumerator.MoveNextAsync().AsTask();
        cancelled.Cancel();
        await ((Func<Task>)(() => pending)).Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync();

        probe!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DuringMoveNext_WaitsForTheActiveEnumeratorBeforeReleasingScope() {
        Probe? probe = null;
        var gate = new Gate();
        using var provider = new ServiceCollection()
            .AddSingleton(gate)
            .AddScoped<Probe>(_ => probe = new Probe())
            .AddScoped<IStreamHandler<Request, int>, BlockingHandler>()
            .BuildServiceProvider();
        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
        var enumerator = start.Value.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var moving = enumerator.MoveNextAsync().AsTask();
        await gate.Entered.Task;

        var disposal = start.Value.DisposeAsync().AsTask();
        disposal.IsCompleted.Should().BeFalse();
        probe!.Disposed.Should().BeFalse();

        gate.Release.TrySetResult();
        (await moving).Should().BeTrue();
        await enumerator.DisposeAsync();
        await disposal;
        probe.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsyncEnumerator_AfterDisposeBegins_FailsClosed() {
        using var provider = new ServiceCollection()
            .AddScoped<Probe>()
            .AddScoped<IStreamHandler<Request, int>, Handler>()
            .BuildServiceProvider();
        var start = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);

        await start.Value.DisposeAsync();
        var act = () => start.Value.GetAsyncEnumerator();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task RejectedStartup_AttemptsThrowingScopeDisposalExactlyOnce() {
        var provider = new ThrowingScopeRootProvider();

        Func<Task> invoke = async () =>
            _ = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);

        await invoke.Should().ThrowAsync<InvalidOperationException>().WithMessage("scope cleanup");
        provider.Scope.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Invoker_ObservedFaultCancellationAndCleanupFailure_DisposeTheInnerEnumeratorExactlyOnce() {
        var faulting = new TrackingStream(_ => ValueTask.FromException<bool>(new InvalidOperationException("move")));
        using (var provider = CreateObservedProvider(faulting)) {
            var started = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
            var enumerator = started.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

            Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
            await move.Should().ThrowAsync<InvalidOperationException>();
            await enumerator.DisposeAsync();
            faulting.DisposeCount.Should().Be(1);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelling = new TrackingStream(_ => ValueTask.FromException<bool>(new OperationCanceledException("cancelled")));
        using (var provider = CreateObservedProvider(cancelling)) {
            var started = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
            var enumerator = started.Value!.GetAsyncEnumerator(cancelled.Token);

            Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
            await move.Should().ThrowAsync<OperationCanceledException>();
            await enumerator.DisposeAsync();
            cancelling.DisposeCount.Should().Be(1);
        }

        var cleanupFailure = new TrackingStream(_ => ValueTask.FromResult(false), new InvalidOperationException("cleanup"));
        using (var provider = CreateObservedProvider(cleanupFailure)) {
            var started = await StreamHandlerInvoker.InvokeAsync<Request, int>(provider, new Request(), ct: TestContext.Current.CancellationToken);
            var enumerator = started.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

            Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
            await move.Should().ThrowAsync<InvalidOperationException>().WithMessage("cleanup");
            await enumerator.DisposeAsync();
            cleanupFailure.DisposeCount.Should().Be(1);
        }
    }

    private static ServiceProvider CreateObservedProvider(TrackingStream stream) => new ServiceCollection()
        .AddSingleton(stream)
        .AddScoped<TrackingHandler>()
        .AddScoped<IStreamHandler<Request, int>>(sp => new StreamObservabilityDecorator<Request, int>(
            sp.GetRequiredService<TrackingHandler>(), "invoker-observed",
            new StreamHandlerMetadata(typeof(TrackingHandler), typeof(Request), typeof(int)), [], loggerFactory: null))
        .BuildServiceProvider();

    private sealed class TrackingHandler(TrackingStream stream) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(stream));
    }

    private sealed class TrackingStream : IAsyncEnumerable<int> {
        private readonly Func<CancellationToken, ValueTask<bool>> _move;
        private readonly Exception? _disposeFailure;
        private int _disposeCount;

        public TrackingStream(Func<CancellationToken, ValueTask<bool>> move, Exception? disposeFailure = null) {
            _move = move;
            _disposeFailure = disposeFailure;
        }

        public int DisposeCount => _disposeCount;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(this, cancellationToken);

        private sealed class Enumerator(TrackingStream owner, CancellationToken cancellationToken) : IAsyncEnumerator<int> {
            public int Current => 0;
            public ValueTask<bool> MoveNextAsync() => owner._move(cancellationToken);
            public ValueTask DisposeAsync() {
                Interlocked.Increment(ref owner._disposeCount);
                return owner._disposeFailure is null
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(owner._disposeFailure);
            }
        }
    }

    private sealed class ThrowingScopeRootProvider : IServiceProvider, IServiceScopeFactory {
        public ThrowingScope Scope { get; } = new();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope() => Scope;
    }

    private sealed class ThrowingScope : IServiceScope, IAsyncDisposable {
        private readonly ScopeServiceProvider _provider = new();
        private int _disposeCount;
        public int DisposeCount => _disposeCount;
        public IServiceProvider ServiceProvider => _provider;
        public void Dispose() => throw new InvalidOperationException("The async path should be used.");
        public ValueTask DisposeAsync() {
            Interlocked.Increment(ref _disposeCount);
            throw new InvalidOperationException("scope cleanup");
        }
    }

    private sealed class ScopeServiceProvider : IServiceProvider {
        public object? GetService(Type serviceType) {
            if (serviceType == typeof(IStreamHandler<Request, int>))
                return new RejectingHandler();
            if (serviceType == typeof(IEnumerable<IDispatchScopeInitializer>))
                return Array.Empty<IDispatchScopeInitializer>();
            return null;
        }
    }
}
