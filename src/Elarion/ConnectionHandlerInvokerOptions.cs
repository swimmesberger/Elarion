namespace Elarion;

/// <summary>
/// How a <see cref="ConnectionHandlerInvoker"/> scopes its dispatches.
/// </summary>
public enum ConnectionDispatchScopeMode {
    /// <summary>
    /// The default: every message dispatches in a fresh seeded DI scope, exactly like every other transport.
    /// Scoped services (unit of work, current user, enrichers) live for one message; the classical
    /// transaction-per-message semantics hold.
    /// </summary>
    PerMessage = 0,

    /// <summary>
    /// The low-allocation mode for high-rate connections: one DI scope is created lazily on first dispatch and
    /// reused for every message; the composed handler chain is resolved once per request type and cached.
    /// Scope initializers still run per message (identity promotion is observed), but <b>scoped services live
    /// for the connection's lifetime</b> — a handler pipeline that assumes per-message scoping (an EF
    /// <c>DbContext</c> behind the transaction decorator, idempotency's unit of work) keeps its state across
    /// every message on the connection. The invoker warns once per handler type when such a pipeline is
    /// dispatched in this mode. Dispatch must be sequential (the adapter's single receive loop), and the owner
    /// that constructed the invoker must dispose it when the connection closes.
    /// </summary>
    PerConnection = 1
}

/// <summary>
/// Construction options for <see cref="ConnectionHandlerInvoker"/>, chosen per connection — a gateway can run
/// a chatty game-style connection with <see cref="ConnectionDispatchScopeMode.PerConnection"/> while every
/// other connection keeps the default per-message semantics.
/// </summary>
public sealed record ConnectionHandlerInvokerOptions {
    /// <summary>How dispatches are scoped. Default <see cref="ConnectionDispatchScopeMode.PerMessage"/>.</summary>
    public ConnectionDispatchScopeMode ScopeMode { get; init; } = ConnectionDispatchScopeMode.PerMessage;
}
