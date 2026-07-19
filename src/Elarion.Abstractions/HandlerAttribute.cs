namespace Elarion.Abstractions;

/// <summary>
/// Exposes a handler class as a <b>named operation</b> on the name-routed transports (JSON-RPC, MCP, …),
/// dispatched through the transport-neutral handler dispatcher. The class must also implement
/// <see cref="IHandler{TRequest, TResponse}"/>.
/// </summary>
/// <remarks>
/// The operation name is transport-neutral — declared once and shared by every name-routed transport. It is
/// <b>optional</b>: when omitted, the generator infers it by convention from the module and the handler/request
/// type name (e.g. module <c>Clients</c> + <c>CreateClient</c> → <c>clients.createClient</c>). Specify an
/// explicit name for stable public contracts. Use <see cref="Transports"/> to choose which transports expose
/// the handler. REST is a separate opt-in via <c>[HttpEndpoint]</c>.
/// </remarks>
/// <example>
/// <code>
/// [Handler]                                                  // inferred name; JSON-RPC + MCP (default)
/// public sealed class CreateClient(...) : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
///
/// [Handler("clients.list", Transports = HandlerTransports.JsonRpc)]  // explicit name, JSON-RPC only
/// public sealed class ListClients(...) : IHandler&lt;ListClients.Query, Result&lt;ListClients.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HandlerAttribute : Attribute {
    /// <summary>Creates the attribute with an inferred operation name.</summary>
    public HandlerAttribute() {
    }

    /// <summary>Creates the attribute with an explicit operation name (e.g. <c>"clients.create"</c>).</summary>
    public HandlerAttribute(string name) {
        Name = name;
    }

    /// <summary>
    /// The explicit operation name, or <see langword="null"/> to let the generator infer it by convention.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// The name-routed transports that expose this handler. Defaults to <see cref="HandlerTransports.All"/>
    /// (JSON-RPC and MCP).
    /// </summary>
    public HandlerTransports Transports { get; init; } = HandlerTransports.All;

    /// <summary>
    /// The handler's registration lifetime — the same vocabulary and semantics as
    /// <see cref="ServiceAttribute.Scope"/>. Defaults to <see cref="ServiceScope.Scoped"/> (one instance per
    /// dispatch scope, the classical unit-of-work-per-message shape).
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceScope.Singleton"/> removes scope participation from the handler's dispatch entirely —
    /// the low-allocation choice for high-rate messages (ADR-0066) — and is compile-time verified: every
    /// constructor dependency must be provably singleton (<c>ELSG011</c>/<c>ELSG012</c>) and the pipeline must
    /// not attach scope-dependent features such as transactions, idempotency, authorization, validation,
    /// caching, or auditing (<c>ELSG013</c>). Per-caller log enrichment is unavailable on a singleton handler
    /// (its chain is built once from the root provider); the span and execution metric remain.
    /// </remarks>
    public ServiceScope Scope { get; init; } = ServiceScope.Scoped;
}
