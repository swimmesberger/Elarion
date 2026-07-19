using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Elarion.Pipeline;

/// <summary>
/// The single always-on observability layer for every generated handler: it opens the handler's OpenTelemetry
/// span and execution metrics <b>and</b> runs the registered <see cref="IHandlerContextEnricher"/> instances
/// (tagging the span, opening one log scope) — the merge of the former <c>TracingDecorator</c> and
/// <c>HandlerContextEnrichmentDecorator</c>.
/// </summary>
/// <remarks>
/// <para>
/// The handler registration generator applies this as the <b>outermost</b> decorator for every handler, so the
/// span parents any cache/resilience/pipeline child spans and the log scope wraps the
/// authorization/validation/handler chain — a denied or invalid request is still attributed to its caller,
/// across <b>every</b> transport (JSON-RPC, HTTP, MCP, scheduler jobs, event consumers). Merging the two
/// always-on decorators into one removes an object allocation per handler resolution on the framework's hottest
/// path (see ADR-0059); enrichment used to be a second decorator immediately inside tracing, and the observable
/// behavior is unchanged.
/// </para>
/// <para>
/// Only bounded metadata is recorded on the span/metric: the handler name, the request type name, the
/// success/error/exception outcome, and the composed decorator pipeline
/// (<c>elarion.handler.pipeline</c>, from <see cref="HandlerMetadata.Pipeline"/>) — never the request/response
/// payload. Caller identity from the enrichers (<c>user.id</c>/<c>user.roles</c>/…) rides the <b>span</b> and the
/// log scope only, never the metric (unbounded cardinality). When no listener is attached to
/// <see cref="HandlerTelemetry.Source"/>, no span is started; when no enricher contributes anything, no scope is
/// opened — both are straight pass-throughs.
/// </para>
/// <para>
/// The stateless behavior lives in <see cref="HandlerObservability"/> as a static method, so it can be reused
/// (e.g. by a future compile-time-composed handler) without allocating this wrapper. This wrapper only carries
/// the per-request <c>inner</c> reference and the per-instance rendered-pipeline-tag cache.
/// </para>
/// </remarks>
public sealed class ObservabilityDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    string handlerName,
    HandlerMetadata metadata,
    IEnumerable<IHandlerContextEnricher> enrichers,
    ILoggerFactory? loggerFactory
) : IHandler<TRequest, TResponse> {
    // The rendered pipeline tag is constant per HANDLER, so compute it lazily and reuse it — no per-request
    // string work. Deliberately an instance field, not static: two handlers sharing TRequest/TResponse (e.g.
    // two handler-form consumers of one event) share this closed generic type but have different metadata
    // pipelines, and a static cache would report one handler's pipeline for the other. A concurrent first
    // render is benign (same value per instance).
    private string? _pipelineTag;

    /// <inheritdoc />
    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        // Render the pipeline tag only when a listener is attached — otherwise a string would be built on every
        // handler call despite no span being started. The caller owns this per-instance cache; the static core
        // stays pure. HasListeners rather than a cached name because two handlers sharing TRequest/TResponse share
        // this closed generic. (A rare listener-attach race between here and the core's own HasListeners check just
        // drops or wastes the tag on one call — benign.)
        var pipelineTag = HandlerTelemetry.Source.HasListeners()
            ? _pipelineTag ??= HandlerObservability.RenderPipeline(metadata.Pipeline)
            : null;

        return HandlerObservability.InvokeAsync(
            inner, handlerName, pipelineTag, enrichers, loggerFactory, request, ct);
    }
}

/// <summary>
/// The stateless core of <see cref="ObservabilityDecorator{TRequest,TResponse}"/>: starts the handler
/// span, runs the enrichers, opens the log scope, invokes the inner handler, and records the execution metric —
/// all without capturing any per-request state, so it is callable statically from anywhere that already holds the
/// inner handler (the decorator wrapper today; a compile-time-composed handler in the future).
/// </summary>
internal static class HandlerObservability {
    private const string LoggerCategory = "Elarion.Diagnostics.HandlerContextEnrichment";

    /// <summary>
    /// Runs <paramref name="inner"/> wrapped in the handler span, context enrichment, log scope, and execution
    /// metric. <paramref name="pipelineTag"/> is the pre-rendered <c>elarion.handler.pipeline</c> value (the
    /// caller resolves it only when a listener is attached; <see langword="null"/> omits the tag).
    /// </summary>
    /// <remarks>Pooled state machine (ADR-0066): the decorator wraps every generated handler, so its
    /// suspension — the common case for any handler that awaits I/O — must not allocate.</remarks>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(
        typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        IHandler<TRequest, TResponse> inner,
        string handlerName,
        string? pipelineTag,
        IEnumerable<IHandlerContextEnricher> enrichers,
        ILoggerFactory? loggerFactory,
        TRequest request,
        CancellationToken ct) {
        // Interpolate the span name only when a listener is attached — otherwise StartActivity returns null and a
        // string would be built on every handler call (the hot path).
        using var activity = HandlerTelemetry.Source.HasListeners()
            ? HandlerTelemetry.Source.StartActivity($"handle {handlerName}", ActivityKind.Internal)
            : null;
        if (activity is not null) {
            activity.SetTag("elarion.handler", handlerName);
            activity.SetTag("elarion.handler.request_type", typeof(TRequest).Name);
            if (pipelineTag is not null) activity.SetTag("elarion.handler.pipeline", pipelineTag);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try {
            // Enrichment runs inside the try so a throwing enricher is recorded as an "exception" outcome, exactly
            // as it was when enrichment was a decorator inside the tracing decorator's try. It runs before the log
            // scope opens (the enrichers decide the scope's contents) and its time is included in the duration.
            IDisposable? logScope = null;
            var list = AsList(enrichers);
            if (list.Count > 0) {
                var context = new HandlerEnrichmentContext();
                for (var i = 0; i < list.Count; i++) list[i].Enrich(context);

                // Traces: tag the current span. Activity.Current is the span started above when a listener is
                // attached; otherwise it is whatever ambient span the transport started (e.g. the ASP.NET request
                // span), which is still the correct enrichment target — matching the former decorator's behavior.
                var tags = context.Tags;
                if (tags.Count > 0 && Activity.Current is { } current)
                    for (var i = 0; i < tags.Count; i++)
                        current.SetTag(tags[i].Key, tags[i].Value);

                // Logs: open one scope over the remaining pipeline so every log line carries the context. Only when
                // an enricher contributed scope items and a logger factory is available.
                var scopeItems = context.ScopeItems;
                if (scopeItems.Count > 0)
                    logScope = loggerFactory?.CreateLogger(LoggerCategory)?.BeginScope(scopeItems);
            }

            TResponse response;
            using (logScope) {
                response = await inner.HandleAsync(request, ct).ConfigureAwait(false);
            }

            // Note: only the success/failure outcome is recorded — never the response payload.
            var outcome = response is IResultLike { IsSuccess: false } ? "error" : "ok";
            if (activity is not null) {
                activity.SetTag("elarion.handler.outcome", outcome);
                if (outcome == "error") activity.SetStatus(ActivityStatusCode.Error);
            }

            HandlerTelemetry.RecordExecution(
                handlerName, outcome, Stopwatch.GetElapsedTime(startTimestamp));
            return response;
        }
        catch (Exception ex) {
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
    // "Decorator" suffix and the generic arity marker for readability (e.g. "Observability,Authorization,Transaction").
    // A conditionally-attached decorator (soft service / AppliesTo) is marked with a trailing "?".
    internal static string RenderPipeline(IHandlerPipeline pipeline) {
        var steps = pipeline.Steps;
        if (steps.Count == 0) return "";

        var parts = new string[steps.Count];
        for (var i = 0; i < steps.Count; i++) {
            var name = steps[i].Decorator.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            if (name.EndsWith("Decorator", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Decorator".Length);

            parts[i] = steps[i].Conditional ? name + "?" : name;
        }

        return string.Join(",", parts);
    }

    private static IReadOnlyList<IHandlerContextEnricher> AsList(IEnumerable<IHandlerContextEnricher> enrichers) {
        // Microsoft DI resolves IEnumerable<T> to a T[]; take the fast cast and only materialize a foreign enumerable.
        if (enrichers is IReadOnlyList<IHandlerContextEnricher> list) return list;

        var materialized = new List<IHandlerContextEnricher>();
        foreach (var enricher in enrichers) materialized.Add(enricher);

        return materialized;
    }
}
