using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Elarion.Pipeline;

/// <summary>Observes stream startup and lazy enumeration as one execution without retaining ambient context.</summary>
public sealed class StreamObservabilityDecorator<TRequest, TItem>(
    IStreamHandler<TRequest, TItem> inner,
    string handlerName,
    StreamHandlerMetadata metadata,
    IEnumerable<IHandlerContextEnricher> enrichers,
    ILoggerFactory? loggerFactory
) : IStreamHandler<TRequest, TItem> {
    public async ValueTask<Result<IAsyncEnumerable<TItem>>> HandleAsync(TRequest request, CancellationToken ct) {
        var parent = Activity.Current;
        var activity = HandlerTelemetry.Source.HasListeners()
            ? HandlerTelemetry.Source.StartActivity($"stream {handlerName}", ActivityKind.Internal)
            : null;
        Configure(activity);
        var observation = new Observation(handlerName, activity, enrichers, loggerFactory);
        try {
            using var scope = observation.BeginEnrichmentScope();
            var started = await inner.HandleAsync(request, ct).ConfigureAwait(false);
            if (!started.IsSuccess) {
                observation.Complete("error");
                return started;
            }

            return Result<IAsyncEnumerable<TItem>>.Success(new ObservedStream(observation, started.Value!));
        }
        catch (Exception exception) {
            observation.Complete("exception", exception);
            throw;
        }
        finally {
            // StartActivity made this activity ambient for startup. The logical execution remains active in the
            // observation, but it must not escape from this eager call into an unrelated lazy continuation.
            Activity.Current = parent;
        }
    }

    private void Configure(Activity? activity) {
        if (activity is null)
            return;
        activity.SetTag("elarion.handler", handlerName);
        activity.SetTag("elarion.handler.request_type", typeof(TRequest).Name);
        activity.SetTag("elarion.handler.pipeline", HandlerObservability.RenderPipeline(metadata.Pipeline));
    }

    private sealed class ObservedStream(
        Observation observation,
        IAsyncEnumerable<TItem> source)
        : IAsyncEnumerable<TItem>, IStreamInvocationObservation {
        public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            try {
                return new Enumerator(observation, source.GetAsyncEnumerator(cancellationToken), cancellationToken);
            }
            catch (Exception exception) {
                observation.Complete("exception", exception);
                throw;
            }
        }

        public void Abandon() {
            observation.Complete("abandoned");
        }
    }

    private sealed class Enumerator(
        Observation observation,
        IAsyncEnumerator<TItem> inner,
        CancellationToken enumerationCancellationToken) : IAsyncEnumerator<TItem> {
        private int _completed;
        private int _innerDisposed;

        public TItem Current => inner.Current;

        public async ValueTask<bool> MoveNextAsync() {
            try {
                bool moved;
                // Completion must follow every part of the successful terminal cleanup. In particular an
                // iterator's finally block or a logger scope can fail while unwinding after MoveNext=false.
                using (observation.Enter()) {
                    using (observation.BeginEnrichmentScope()) {
                        moved = await inner.MoveNextAsync().ConfigureAwait(false);
                        if (!moved) await DisposeInnerAsync().ConfigureAwait(false);
                    }
                }

                if (!moved)
                    Complete("ok");
                return moved;
            }
            catch (Exception exception) {
                await CompleteFaultedMoveAsync(exception).ConfigureAwait(false);
                throw new UnreachableException();
            }
        }

        public async ValueTask DisposeAsync() {
            var failure = await DisposeInnerObservedAsync().ConfigureAwait(false);

            if (failure is null) {
                // A consumer breaking an await-foreach is a normal abandoned stream, not a handler exception.
                Complete("abandoned");
                return;
            }

            // This includes an exception thrown while disposing a logger enrichment scope: the stream did not
            // finish cleanly, and the activity/metric must retain that failure instead of being abandoned.
            Complete("exception", failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private async ValueTask CompleteFaultedMoveAsync(Exception failure) {
            // MoveNextAsync may fault before an await-foreach gets a chance to run its finally. The inner
            // enumerator still owns iterator cleanup. Context/enrichment failures are recorded as cleanup
            // failures, but must never prevent the one inner DisposeAsync attempt.
            if (await DisposeInnerObservedAsync().ConfigureAwait(false) is { } cleanupFailure) {
                Complete("exception", cleanupFailure);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            if (failure is OperationCanceledException && enumerationCancellationToken.IsCancellationRequested)
                Complete("cancelled");
            else
                Complete("exception", failure);

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private ValueTask DisposeInnerAsync() {
            return Interlocked.Exchange(ref _innerDisposed, 1) == 0
                ? inner.DisposeAsync()
                : ValueTask.CompletedTask;
        }

        private async ValueTask<Exception?> DisposeInnerObservedAsync() {
            if (Interlocked.Exchange(ref _innerDisposed, 1) != 0)
                return null;

            Exception? failure = null;
            IDisposable? context = null;
            IDisposable? scope = null;
            try {
                context = observation.Enter();
            }
            catch (Exception exception) {
                failure = Combine(failure, exception);
            }

            try {
                scope = observation.BeginEnrichmentScope();
            }
            catch (Exception exception) {
                failure = Combine(failure, exception);
            }

            try {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) {
                failure = Combine(failure, exception);
            }

            try {
                scope?.Dispose();
            }
            catch (Exception exception) {
                failure = Combine(failure, exception);
            }

            try {
                context?.Dispose();
            }
            catch (Exception exception) {
                failure = Combine(failure, exception);
            }

            return failure;
        }

        private static Exception Combine(Exception? current, Exception next) {
            return current is null ? next : new AggregateException(current, next);
        }

        private void Complete(string outcome, Exception? exception = null) {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                observation.Complete(outcome, exception);
        }
    }

    private sealed class Observation(
        string handlerName,
        Activity? activity,
        IEnumerable<IHandlerContextEnricher> enrichers,
        ILoggerFactory? loggerFactory) {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _completed;

        public IDisposable Enter() {
            return new AmbientActivity(activity);
        }

        public IDisposable? BeginEnrichmentScope() {
            var context = new HandlerEnrichmentContext();
            var any = false;
            foreach (var enricher in enrichers) {
                enricher.Enrich(context);
                any = true;
            }

            if (any && context.Tags.Count > 0 && Activity.Current is { } current)
                foreach (var tag in context.Tags)
                    current.SetTag(tag.Key, tag.Value);
            return context.ScopeItems.Count == 0
                ? null
                : loggerFactory?.CreateLogger("Elarion.Diagnostics.HandlerContextEnrichment")
                    .BeginScope(context.ScopeItems);
        }

        public void Complete(string outcome, Exception? exception = null) {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            if (activity is not null) {
                var previous = Activity.Current;
                Activity.Current = activity;
                activity.SetTag("elarion.handler.outcome", outcome);
                if (exception is not null)
                    activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                        { "exception.type", exception.GetType().FullName }, { "exception.message", exception.Message }
                    }));
                if (outcome is "error" or "exception")
                    activity.SetStatus(ActivityStatusCode.Error, exception?.Message);
                activity.Stop();
                Activity.Current = previous;
            }

            HandlerTelemetry.RecordExecution(handlerName, outcome, Stopwatch.GetElapsedTime(_started));
        }

        private sealed class AmbientActivity : IDisposable {
            private readonly Activity? _previous;

            public AmbientActivity(Activity? activity) {
                _previous = Activity.Current;
                if (activity is not null)
                    Activity.Current = activity;
            }

            public void Dispose() {
                Activity.Current = _previous;
            }
        }
    }
}
