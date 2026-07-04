using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>
/// Wraps a handler invocation in an OpenTelemetry span and execution metrics.
/// </summary>
/// <remarks>
/// Applied by the handler registration generator as the outermost decorator for every generated
/// handler, so the span parents any cache, resilience, or pipeline child spans. Only bounded metadata is recorded:
/// the handler name, the request type name, and the success/error/exception outcome — never the
/// request or response payload. When no listener is attached to <see cref="HandlerTelemetry.Source"/>,
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns <see langword="null"/> and
/// the decorator adds only the execution metric.
/// </remarks>
public sealed class TracingDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    string handlerName
) : IHandler<TRequest, TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        using var activity = HandlerTelemetry.Source.StartActivity(
            $"handle {handlerName}", ActivityKind.Internal);
        if (activity is not null) {
            activity.SetTag("elarion.handler", handlerName);
            activity.SetTag("elarion.handler.request_type", typeof(TRequest).Name);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try {
            var response = await inner.HandleAsync(request, ct);

            // Note: only the success/failure outcome is recorded — never the response payload.
            var outcome = response is IResultLike { IsSuccess: false } ? "error" : "ok";
            if (activity is not null) {
                activity.SetTag("elarion.handler.outcome", outcome);
                if (outcome == "error") {
                    activity.SetStatus(ActivityStatusCode.Error);
                }
            }

            HandlerTelemetry.RecordExecution(
                handlerName, outcome, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
            return response;
        } catch (Exception ex) {
            if (activity is not null) {
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                    { "exception.type", ex.GetType().FullName },
                    { "exception.message", ex.Message }
                }));
                activity.SetTag("elarion.handler.outcome", "exception");
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            }

            HandlerTelemetry.RecordExecution(
                handlerName, "exception", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
            throw;
        }
    }
}
