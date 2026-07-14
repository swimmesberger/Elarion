using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>
/// Wraps a handler invocation in an OpenTelemetry span and execution metrics.
/// </summary>
/// <remarks>
/// Applied by the handler registration generator as the outermost decorator for every generated
/// handler, so the span parents any cache, resilience, or pipeline child spans. Only bounded metadata is recorded:
/// the handler name, the request type name, the success/error/exception outcome, and the composed decorator
/// pipeline (<c>elarion.handler.pipeline</c>, from <see cref="HandlerMetadata.Pipeline"/> — a constant per
/// handler type) — never the request or response payload. When no listener is attached to
/// <see cref="HandlerTelemetry.Source"/>, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <see langword="null"/> and the decorator adds only the execution metric.
/// </remarks>
public sealed class TracingDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    string handlerName,
    HandlerMetadata metadata
) : IHandler<TRequest, TResponse> {
    // The rendered pipeline tag is constant per HANDLER, so compute it lazily and reuse it — no per-request
    // string work. Deliberately an instance field, not static: two handlers sharing TRequest/TResponse (e.g.
    // two handler-form consumers of one event) share this closed generic type but have different metadata
    // pipelines, and a static cache would report one handler's pipeline for the other. A concurrent first
    // render is benign (same value per instance).
    private string? _pipelineTag;

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        // Interpolate the span name only when a listener is attached — otherwise a string would be built
        // on every handler call (the hot path) despite StartActivity returning null. HasListeners rather
        // than a cached name because two handlers sharing TRequest/TResponse share this closed generic.
        using var activity = HandlerTelemetry.Source.HasListeners()
            ? HandlerTelemetry.Source.StartActivity($"handle {handlerName}", ActivityKind.Internal)
            : null;
        if (activity is not null) {
            activity.SetTag("elarion.handler", handlerName);
            activity.SetTag("elarion.handler.request_type", typeof(TRequest).Name);
            activity.SetTag("elarion.handler.pipeline", _pipelineTag ??= RenderPipeline(metadata.Pipeline));
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
                handlerName, outcome, Stopwatch.GetElapsedTime(startTimestamp));
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
                handlerName, "exception", Stopwatch.GetElapsedTime(startTimestamp));
            throw;
        }
    }

    // Renders the resolved pipeline as a comma-separated list of decorator names in execution order, dropping the
    // "Decorator" suffix and the generic arity marker for readability (e.g. "Tracing,Authorization,Transaction").
    // A conditionally-attached decorator (soft service / AppliesTo) is marked with a trailing "?".
    private static string RenderPipeline(IHandlerPipeline pipeline) {
        var steps = pipeline.Steps;
        if (steps.Count == 0) {
            return "";
        }

        var parts = new string[steps.Count];
        for (var i = 0; i < steps.Count; i++) {
            var name = steps[i].Decorator.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) {
                name = name.Substring(0, tick);
            }

            if (name.EndsWith("Decorator", StringComparison.Ordinal)) {
                name = name.Substring(0, name.Length - "Decorator".Length);
            }

            parts[i] = steps[i].Conditional ? name + "?" : name;
        }

        return string.Join(",", parts);
    }
}
