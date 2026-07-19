using System.Runtime.CompilerServices;
using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Diagnostics;
using Elarion.Pipeline;
using Elarion.Tests.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Pipeline;

public sealed class StreamPipelineTests {
    private sealed record Request(bool Reject = false);

    private sealed class ProbeHandler(List<string> steps) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) {
            steps.Add("handler-start");
            return request.Reject
                ? ValueTask.FromResult<Result<IAsyncEnumerable<int>>>(AppError.Validation("rejected"))
                : ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Values()));
        }

        private async IAsyncEnumerable<int> Values([EnumeratorCancellation] CancellationToken ct = default) {
            steps.Add("handler-enumerate");
            yield return 1;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return 2;
        }
    }

    private sealed class RecordingDecorator(
        IStreamHandler<Request, int> inner,
        List<string> steps,
        string name) : IStreamHandler<Request, int> {
        public async ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) {
            steps.Add($"{name}-start");
            var result = await inner.HandleAsync(request, ct);
            return result.IsSuccess ? Result<IAsyncEnumerable<int>>.Success(Wrap(result.Value!)) : result;
        }

        private async IAsyncEnumerable<int> Wrap(IAsyncEnumerable<int> source,
            [EnumeratorCancellation] CancellationToken ct = default) {
            steps.Add($"{name}-enter");
            await foreach (var item in source.WithCancellation(ct)) {
                steps.Add($"{name}-item-{item}");
                yield return item;
            }

            steps.Add($"{name}-complete");
        }
    }

    [Fact]
    public async Task StartupIsEager_WhileItemsRemainLazyAndBackpressured() {
        var steps = new List<string>();
        IStreamHandler<Request, int> handler = new ProbeHandler(steps);
        handler = new RecordingDecorator(handler, steps, "inner");
        handler = new RecordingDecorator(handler, steps, "outer");

        var started = await handler.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        steps.Should().Equal("outer-start", "inner-start", "handler-start");
        await using var enumerator = started.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(1);
        steps.Should().Equal("outer-start", "inner-start", "handler-start", "outer-enter", "inner-enter",
            "handler-enumerate", "inner-item-1", "outer-item-1");
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().Be(2);
        (await enumerator.MoveNextAsync()).Should().BeFalse();
        steps.Should().ContainInOrder("inner-complete", "outer-complete");
    }

    [Fact]
    public async Task RejectedStartup_DoesNotEnumerateTheHandler() {
        var steps = new List<string>();
        var handler = new RecordingDecorator(new ProbeHandler(steps), steps, "outer");

        var result = await handler.HandleAsync(new Request(true), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        steps.Should().Equal("outer-start", "handler-start");
    }

    [Fact]
    public async Task Observability_CompletedAndEarlyDisposedStreams_RecordHonestTerminalOutcomes() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = new StreamObservabilityDecorator<Request, int>(
            new ProbeHandler([]), "stream-test",
            new StreamHandlerMetadata(typeof(ProbeHandler), typeof(Request), typeof(int)), [], null);

        var completed = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        await foreach (var _ in completed.Value!) {
        }

        var abandoned = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        await using (var enumerator = abandoned.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken)) {
            (await enumerator.MoveNextAsync()).Should().BeTrue();
        }

        meters.Measurements.Should().Contain(measurement =>
                measurement.InstrumentName == "handler.execution.count" &&
                measurement.HasTag("elarion.handler.outcome", "ok"))
            .And.Contain(measurement => measurement.InstrumentName == "handler.execution.count" &&
                                        measurement.HasTag("elarion.handler.outcome", "abandoned"));
        activities.Activities.Should().NotContain(activity =>
            Equals(activity.GetTag("elarion.handler.outcome"), "exception") &&
            activity.DisplayName == "stream stream-test");
    }

    [Fact]
    public async Task Observability_EnumeratorCreationAndCleanupFailures_AreExceptions() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = new StreamObservabilityDecorator<Request, int>(
            new FailingStreamHandler(), "failing-stream",
            new StreamHandlerMetadata(typeof(FailingStreamHandler), typeof(Request), typeof(int)), [], null);

        var creation = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var create = () => creation.Value!.GetAsyncEnumerator();
        create.Should().Throw<InvalidOperationException>();

        var cleanup = await decorator.HandleAsync(new Request(false), TestContext.Current.CancellationToken);
        // The second accepted stream fails on DisposeAsync rather than creation.
        var enumerator = cleanup.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var dispose = () => enumerator.DisposeAsync().AsTask();
        await dispose.Should().ThrowAsync<InvalidOperationException>();

        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler.outcome", "exception"));
        activities.Activities.Where(activity => activity.DisplayName == "stream failing-stream")
            .Should().OnlyContain(activity => activity.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task Observability_TerminalInnerAndEnrichmentCleanupFailures_AreExceptions_NotOk() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var innerFailure = new StreamObservabilityDecorator<Request, int>(new FailingStreamHandler(), "terminal-inner",
            new StreamHandlerMetadata(typeof(FailingStreamHandler), typeof(Request), typeof(int)), [], null);
        // First accepted stream fails at enumerator creation; the second fails while terminal MoveNext cleans up.
        var ignored = await innerFailure.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        Action create = () => ignored.Value!.GetAsyncEnumerator();
        create.Should().Throw<InvalidOperationException>();
        var terminal = await innerFailure.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        await using (var enumerator = terminal.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken)) {
            Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
            await move.Should().ThrowAsync<InvalidOperationException>();
        }

        var scopeFailure = new StreamObservabilityDecorator<Request, int>(new EmptyStreamHandler(), "terminal-scope",
            new StreamHandlerMetadata(typeof(EmptyStreamHandler), typeof(Request), typeof(int)), [new ScopeEnricher()],
            new ThrowOnSecondScopeLoggerFactory());
        var scoped = await scopeFailure.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        await using (var enumerator = scoped.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken)) {
            Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
            await move.Should().ThrowAsync<InvalidOperationException>();
        }

        meters.Measurements.Count(m => m.InstrumentName == "handler.execution.count" &&
                                       m.HasTag("elarion.handler.outcome", "exception")).Should().Be(3);
        activities.Activities.Where(a => a.DisplayName is "stream terminal-inner" or "stream terminal-scope")
            .Should().OnlyContain(a => Equals(a.GetTag("elarion.handler.outcome"), "exception"));
    }

    [Fact]
    public async Task Observability_FaultedAndCancelledEnumerators_DisposeInnerExactlyOnce() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var faulting = new TrackingStream(_ => ValueTask.FromException<bool>(new InvalidOperationException("move")));
        var faultDecorator = CreateObservabilityDecorator(faulting, "faulted-disposal");
        var faulted = await faultDecorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var faultEnumerator = faulted.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Func<Task> move = () => faultEnumerator.MoveNextAsync().AsTask();
        await move.Should().ThrowAsync<InvalidOperationException>();
        await faultEnumerator.DisposeAsync();
        faulting.DisposeCount.Should().Be(1);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelling =
            new TrackingStream(_ => ValueTask.FromException<bool>(new OperationCanceledException("cancelled")));
        var cancelDecorator = CreateObservabilityDecorator(cancelling, "cancelled-disposal");
        var cancelledResult = await cancelDecorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var cancelledEnumerator = cancelledResult.Value!.GetAsyncEnumerator(cancelled.Token);

        Func<Task> cancelledMove = () => cancelledEnumerator.MoveNextAsync().AsTask();
        await cancelledMove.Should().ThrowAsync<OperationCanceledException>();
        await cancelledEnumerator.DisposeAsync();
        cancelling.DisposeCount.Should().Be(1);
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.execution.count" &&
                                                  m.HasTag("elarion.handler.outcome", "exception") &&
                                                  m.HasTag("elarion.handler", "faulted-disposal"));
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.execution.count" &&
                                                  m.HasTag("elarion.handler.outcome", "cancelled") &&
                                                  m.HasTag("elarion.handler", "cancelled-disposal"));
    }

    [Fact]
    public async Task Observability_UnsolicitedOperationCanceledException_IsAnExceptionAndCleanupFailureWins() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var unsolicited =
            new TrackingStream(_ => ValueTask.FromException<bool>(new OperationCanceledException("unsolicited")));
        var decorator = CreateObservabilityDecorator(unsolicited, "unsolicited-cancellation");
        var started = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var enumerator = started.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
        await move.Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync();

        unsolicited.DisposeCount.Should().Be(1);
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.execution.count" &&
                                                  m.HasTag("elarion.handler.outcome", "exception") &&
                                                  m.HasTag("elarion.handler", "unsolicited-cancellation"));
        activities.Activities.Should().ContainSingle(a => a.DisplayName == "stream unsolicited-cancellation" &&
                                                          Equals(a.GetTag("elarion.handler.outcome"), "exception") &&
                                                          a.Status == ActivityStatusCode.Error);

        var cleanupFailure = new TrackingStream(
            _ => ValueTask.FromResult(false),
            new InvalidOperationException("cleanup"));
        var cleanupDecorator = CreateObservabilityDecorator(cleanupFailure, "cleanup-failure");
        var cleanupStarted = await cleanupDecorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var cleanupEnumerator = cleanupStarted.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Func<Task> cleanupMove = () => cleanupEnumerator.MoveNextAsync().AsTask();
        await cleanupMove.Should().ThrowAsync<InvalidOperationException>().WithMessage("cleanup");
        await cleanupEnumerator.DisposeAsync();
        cleanupFailure.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Observability_UsesOnlyEnumerationTokenToClassifyCancellation() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        using var startup = new CancellationTokenSource();
        var source =
            new TrackingStream(_ => ValueTask.FromException<bool>(new OperationCanceledException("unsolicited")));
        var decorator = CreateObservabilityDecorator(source, "startup-token-cancelled");
        var started = await decorator.HandleAsync(new Request(), startup.Token);
        startup.Cancel();
        var enumerator = started.Value!.GetAsyncEnumerator(CancellationToken.None);

        Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
        await move.Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync();

        source.DisposeCount.Should().Be(1);
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.execution.count" &&
                                                  m.HasTag("elarion.handler.outcome", "exception") &&
                                                  m.HasTag("elarion.handler", "startup-token-cancelled"));
        meters.Measurements.Should().NotContain(m => m.InstrumentName == "handler.execution.count" &&
                                                     m.HasTag("elarion.handler.outcome", "cancelled") &&
                                                     m.HasTag("elarion.handler", "startup-token-cancelled"));
    }

    [Fact]
    public async Task Observability_EnrichmentSetupFailureStillDisposesInnerExactlyOnce() {
        var source = new TrackingStream(_ => ValueTask.FromResult(false));
        var decorator = new StreamObservabilityDecorator<Request, int>(new StaticStreamHandler(source),
            "enrichment-setup",
            new StreamHandlerMetadata(typeof(StaticStreamHandler), typeof(Request), typeof(int)), [new ScopeEnricher()],
            new ThrowOnSecondBeginScopeLoggerFactory());
        var started = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        var enumerator = started.Value!.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Func<Task> move = () => enumerator.MoveNextAsync().AsTask();
        await move.Should().ThrowAsync<InvalidOperationException>().WithMessage("scope setup");
        await enumerator.DisposeAsync();

        source.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Authorization_ResourceBinding_IsResolvedAndDeniedBeforeTheStreamStarts() {
        var authorizer = new RecordingAuthorizer(AppError.Forbidden("denied"));
        var steps = new List<string>();
        var handler = new StreamAuthorizationDecorator<ResourceRequest, int>(
            new ResourceHandler(steps),
            new StreamHandlerMetadata(typeof(ResourceHandler), typeof(ResourceRequest), typeof(int)), authorizer,
            resourceBindings: [
                new ResourceRequirementBinding<ResourceRequest>(typeof(Resource), new ResourceOperation("read"),
                    static request => request.Owner.Id)
            ]);

        var result =
            await handler.HandleAsync(new ResourceRequest(new Owner(42)), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        authorizer.Requirements.Resources.Should().ContainSingle().Which.ResourceId.Should().Be(42);
        steps.Should().BeEmpty();
    }

    [Fact]
    public async Task FeatureGate_MixedBlankNameStillEvaluatesEffectiveName_AndRecordsGateTelemetry() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        using var activity = new Activity("stream").Start();
        var flags = new RecordingFeatureFlags(("paid-export", false));
        var decorator = new StreamFeatureGateDecorator<Request, int>(
            new ProbeHandler([]),
            new StreamHandlerMetadata(typeof(MixedFeatureGateHandler), typeof(Request), typeof(int)), flags);

        var result = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        flags.Queried.Should().Equal("paid-export");
        activity.GetTagItem("elarion.feature_gate.outcome").Should().Be("closed");
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.feature_gate.closed.count" &&
                                                  m.HasTag("elarion.handler", nameof(MixedFeatureGateHandler)));
    }

    [Fact]
    public async Task FeatureGate_AllBlankAndNegatedBlankAreInert_WhileNegatedEffectiveGateCloses() {
        var flags = new RecordingFeatureFlags(("paid-export", true));
        var allBlank = new StreamFeatureGateDecorator<Request, int>(new ProbeHandler([]),
            new StreamHandlerMetadata(typeof(AllBlankFeatureGateHandler), typeof(Request), typeof(int)), flags);
        var negatedBlank = new StreamFeatureGateDecorator<Request, int>(new ProbeHandler([]),
            new StreamHandlerMetadata(typeof(NegatedBlankFeatureGateHandler), typeof(Request), typeof(int)), flags);
        var negated = new StreamFeatureGateDecorator<Request, int>(new ProbeHandler([]),
            new StreamHandlerMetadata(typeof(NegatedFeatureGateHandler), typeof(Request), typeof(int)), flags);

        (await allBlank.HandleAsync(new Request(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        (await negatedBlank.HandleAsync(new Request(), TestContext.Current.CancellationToken)).IsSuccess.Should()
            .BeTrue();
        (await negated.HandleAsync(new Request(), TestContext.Current.CancellationToken)).IsSuccess.Should().BeFalse();
        flags.Queried.Should().Equal("paid-export");
    }

    [Fact]
    public async Task AuthorizationDenial_TagsAndRecordsDedicatedTelemetryWithoutGenericExecutionOutcome() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        using var activity = new Activity("stream").Start();
        var decorator = new StreamAuthorizationDecorator<Request, int>(new ProbeHandler([]),
            new StreamHandlerMetadata(typeof(ProbeHandler), typeof(Request), typeof(int)),
            new RecordingAuthorizer(AppError.Unauthorized("no")));

        var result = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        activity.GetTagItem("elarion.authorization.outcome").Should().Be("unauthorized");
        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.authorization.denied.count" &&
                                                  m.HasTag("elarion.authorization.outcome", "unauthorized"));
        meters.Measurements.Should().NotContain(m => m.InstrumentName == "handler.execution.count");
    }

    private sealed record ResourceRequest(Owner Owner);

    private sealed record Owner(int Id);

    private sealed class Resource;

    private sealed class ResourceHandler(List<string> steps) : IStreamHandler<ResourceRequest, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(ResourceRequest request, CancellationToken ct) {
            steps.Add("started");
            return ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Empty()));
        }

        private static async IAsyncEnumerable<int> Empty() {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FailingStreamHandler : IStreamHandler<Request, int> {
        private int _calls;

        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) {
            return ValueTask.FromResult(
                Result<IAsyncEnumerable<int>>.Success(++_calls == 1 ? new CreationFailure() : new DisposeFailure()));
        }
    }

    private sealed class CreationFailure : IAsyncEnumerable<int> {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            throw new InvalidOperationException("creation");
        }
    }

    private sealed class DisposeFailure : IAsyncEnumerable<int>, IAsyncEnumerator<int> {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return this;
        }

        public int Current => 0;

        public ValueTask<bool> MoveNextAsync() {
            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync() {
            return ValueTask.FromException(new InvalidOperationException("cleanup"));
        }
    }

    private sealed class EmptyStreamHandler : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(Empty()));
        }

        private static async IAsyncEnumerable<int> Empty() {
            await Task.Yield();
            yield break;
        }
    }

    private static StreamObservabilityDecorator<Request, int> CreateObservabilityDecorator(
        IAsyncEnumerable<int> stream,
        string name) {
        return new StreamObservabilityDecorator<Request, int>(new StaticStreamHandler(stream), name,
            new StreamHandlerMetadata(typeof(StaticStreamHandler), typeof(Request), typeof(int)), [], null);
    }

    private sealed class StaticStreamHandler(IAsyncEnumerable<int> stream) : IStreamHandler<Request, int> {
        public ValueTask<Result<IAsyncEnumerable<int>>> HandleAsync(Request request, CancellationToken ct) {
            return ValueTask.FromResult(Result<IAsyncEnumerable<int>>.Success(stream));
        }
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

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return new Enumerator(this, cancellationToken);
        }

        private sealed class Enumerator(TrackingStream owner, CancellationToken cancellationToken)
            : IAsyncEnumerator<int> {
            public int Current => 0;

            public ValueTask<bool> MoveNextAsync() {
                return owner._move(cancellationToken);
            }

            public ValueTask DisposeAsync() {
                Interlocked.Increment(ref owner._disposeCount);
                return owner._disposeFailure is null
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(owner._disposeFailure);
            }
        }
    }

    private sealed class ScopeEnricher : Elarion.Abstractions.Diagnostics.IHandlerContextEnricher {
        public void Enrich(Elarion.Abstractions.Diagnostics.HandlerEnrichmentContext context) {
            context.AddScopeItem("Test", "value");
        }
    }

    private sealed class ThrowOnSecondScopeLoggerFactory : ILoggerFactory {
        private int _scopes;

        public ILogger CreateLogger(string categoryName) {
            return new Logger(this);
        }

        public void AddProvider(ILoggerProvider provider) {
        }

        public void Dispose() {
        }

        private sealed class Logger(ThrowOnSecondScopeLoggerFactory owner) : ILogger {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull {
                return Interlocked.Increment(ref owner._scopes) == 2 ? new ThrowingScope() : NoopScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
            }
        }

        private sealed class NoopScope : IDisposable {
            public static readonly NoopScope Instance = new();

            public void Dispose() {
            }
        }

        private sealed class ThrowingScope : IDisposable {
            public void Dispose() {
                throw new InvalidOperationException("scope cleanup");
            }
        }
    }

    private sealed class ThrowOnSecondBeginScopeLoggerFactory : ILoggerFactory {
        private int _scopes;

        public ILogger CreateLogger(string categoryName) {
            return new Logger(this);
        }

        public void AddProvider(ILoggerProvider provider) {
        }

        public void Dispose() {
        }

        private sealed class Logger(ThrowOnSecondBeginScopeLoggerFactory owner) : ILogger {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull {
                if (Interlocked.Increment(ref owner._scopes) is 2 or 3)
                    throw new InvalidOperationException("scope setup");
                return NoopScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
            }
        }

        private sealed class NoopScope : IDisposable {
            public static readonly NoopScope Instance = new();

            public void Dispose() {
            }
        }
    }

    private sealed class RecordingAuthorizer(AppError? result) : IAuthorizer {
        public AuthorizationRequirements Requirements { get; private set; }

        public ValueTask<AppError?> AuthorizeAsync(AuthorizationRequirements requirements, object? resource,
            CancellationToken ct) {
            Requirements = requirements;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingFeatureFlags(params (string Name, bool Enabled)[] flags) : IFeatureFlagService {
        private readonly Dictionary<string, bool> _flags = flags.ToDictionary(x => x.Name, x => x.Enabled);
        public List<string> Queried { get; } = [];

        public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) {
            Queried.Add(feature);
            return ValueTask.FromResult(_flags.TryGetValue(feature, out var enabled) && enabled);
        }
    }

    [FeatureGate("paid-export", "")]
    private sealed class MixedFeatureGateHandler;

    [FeatureGate("")]
    private sealed class AllBlankFeatureGateHandler;

    [FeatureGate("", Negate = true)]
    private sealed class NegatedBlankFeatureGateHandler;

    [FeatureGate("paid-export", Negate = true)]
    private sealed class NegatedFeatureGateHandler;
}
