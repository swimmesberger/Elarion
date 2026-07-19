namespace Elarion.Abstractions;

/// <summary>
/// How much always-on observability the generated pipeline gives a handler; declared with
/// <see cref="HandlerTelemetryAttribute"/>.
/// </summary>
public enum HandlerTelemetryMode {
    /// <summary>
    /// The default: the observability decorator wraps the handler — span, execution metric, context
    /// enrichment, log scope. Free until a listener attaches.
    /// </summary>
    Full = 0,

    /// <summary>
    /// No observability decorator is generated for the handler at all: no span, no execution metric, no
    /// enrichment, no log scope, and no decorator object on the hot path. For high-rate dispatch (a
    /// game-server-like connection) where even the enrichment run per message is measurable. Failures are
    /// unaffected — they remain <c>Result</c> values and exceptions still propagate to the transport.
    /// </summary>
    None = 1
}
