namespace Elarion.JsonRpc;

/// <summary>
/// A transport-neutral, type-keyed carrier of values captured at a dispatch boundary (the JSON-RPC
/// endpoint, a batch item, or an MCP tool call) and handed to <see cref="IDispatchScopeInitializer"/>
/// instances so they can seed scoped services in the per-call DI scope the dispatcher creates.
/// </summary>
/// <remarks>
/// <para>
/// Dispatcher-based transports dispatch each call in a fresh child scope, which does not inherit the parent
/// scope's scoped instances (only root singletons / <c>AsyncLocal</c> cross that boundary). This context is
/// the explicit, ambient-free way to move request-boundary state into that scope: the transport captures
/// values here, and registered initializers read them back out.
/// </para>
/// <para>
/// It is deliberately generic — not tied to any one concern. The framework stores the authenticated
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> so the default current-user initializer can seed
/// <c>ICurrentUser</c>, but a host can carry any additional per-call state (tenant, correlation id, …) keyed
/// by its own type and consume it from a custom <see cref="IDispatchScopeInitializer"/>.
/// </para>
/// </remarks>
public sealed class DispatchScopeContext {
    private readonly Dictionary<Type, object?> _items = [];

    /// <summary>
    /// A shared, empty context used when a dispatch site has nothing to carry. Treat it as read-only;
    /// do not call <see cref="Set{T}"/> on it.
    /// </summary>
    public static DispatchScopeContext Empty { get; } = new();

    /// <summary>Stores <paramref name="value"/> keyed by <typeparamref name="T"/>, replacing any existing entry.</summary>
    public void Set<T>(T value) => _items[typeof(T)] = value;

    /// <summary>
    /// Gets the value captured for <typeparamref name="T"/>.
    /// </summary>
    /// <param name="value">The captured value, or <see langword="default"/> when none was captured.</param>
    /// <returns><see langword="true"/> when a value of <typeparamref name="T"/> was captured.</returns>
    public bool TryGet<T>(out T? value) {
        if (_items.TryGetValue(typeof(T), out var stored) && stored is T typed) {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}
