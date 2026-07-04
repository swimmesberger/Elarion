using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Elarion.Pipeline;

/// <summary>
/// Runs the registered <see cref="IHandlerContextEnricher"/> instances for a handler execution, applies their trace
/// tags to the handler span, and opens one logging scope carrying their scope items — so every trace and log line
/// produced during the handler can be filtered by user/request context, across <b>every</b> transport (JSON-RPC,
/// HTTP, MCP, scheduler jobs, event consumers), not just HTTP requests.
/// </summary>
/// <remarks>
/// <para>
/// First-class part of handler tracing: the handler registration generator auto-attaches this decorator immediately
/// inside <see cref="TracingDecorator{TRequest,TResponse}"/>, so <see cref="Activity.Current"/> is the
/// <c>handle {handler}</c> span it tags, and its <see cref="ILogger.BeginScope{TState}(TState)"/> wraps the
/// authorization/validation/handler chain — a denied or invalid request is still attributed to its caller.
/// </para>
/// <para>
/// The decorator itself knows nothing about "user": it drains whatever the enrichers contribute. Elarion ships the
/// built-in <see cref="Diagnostics.UserContextEnricher"/> (registered by default with current-user support), and a
/// host adds its own via <c>AddElarionHandlerContextEnricher</c>. It is fully <b>inert</b> (a straight pass-through)
/// when no enricher is registered or none contributes anything. User identity is deliberately kept off metrics
/// (unbounded cardinality); it rides only per-span attributes and the per-execution log scope. Log-scope keys only
/// surface when the host enables <c>IncludeScopes</c> on its logging exporter.
/// </para>
/// </remarks>
public sealed class HandlerContextEnrichmentDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IEnumerable<IHandlerContextEnricher> enrichers,
    ILoggerFactory? loggerFactory
) : IHandler<TRequest, TResponse> {
    private const string LoggerCategory = "Elarion.Diagnostics.HandlerContextEnrichment";

    /// <inheritdoc />
    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var list = AsList(enrichers);
        if (list.Count == 0) {
            return inner.HandleAsync(request, ct);
        }

        var context = new HandlerEnrichmentContext();
        for (var i = 0; i < list.Count; i++) {
            list[i].Enrich(context);
        }

        // Traces: tag the handler span (Activity.Current is TracingDecorator's span, since this runs just inside it).
        var tags = context.Tags;
        if (tags.Count > 0 && Activity.Current is { } activity) {
            for (var i = 0; i < tags.Count; i++) {
                activity.SetTag(tags[i].Key, tags[i].Value);
            }
        }

        // Logs: open one scope over the remaining pipeline so every log line carries the context.
        var scopeItems = context.ScopeItems;
        if (scopeItems.Count == 0) {
            return inner.HandleAsync(request, ct);
        }

        var logger = loggerFactory?.CreateLogger(LoggerCategory);
        if (logger is null) {
            return inner.HandleAsync(request, ct);
        }

        return HandleScopedAsync(logger, scopeItems, request, ct);
    }

    private async ValueTask<TResponse> HandleScopedAsync(
        ILogger logger, IReadOnlyList<KeyValuePair<string, object>> scopeState, TRequest request, CancellationToken ct) {
        using (logger.BeginScope(scopeState)) {
            return await inner.HandleAsync(request, ct).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<IHandlerContextEnricher> AsList(IEnumerable<IHandlerContextEnricher> enrichers) {
        // Microsoft DI resolves IEnumerable<T> to a T[]; take the fast cast and only materialize a foreign enumerable.
        if (enrichers is IReadOnlyList<IHandlerContextEnricher> list) {
            return list;
        }

        var materialized = new List<IHandlerContextEnricher>();
        foreach (var enricher in enrichers) {
            materialized.Add(enricher);
        }

        return materialized;
    }
}
