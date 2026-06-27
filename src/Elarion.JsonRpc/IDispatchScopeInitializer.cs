namespace Elarion.JsonRpc;

/// <summary>
/// A hook that seeds scoped services in a per-call dispatch scope from the captured
/// <see cref="DispatchScopeContext"/>. Implementations are resolved from the call scope and run once per
/// dispatch, immediately after the scope is created and before the handler runs.
/// </summary>
/// <remarks>
/// This is the extension point for carrying request-boundary state into the fresh DI scope dispatcher-based
/// transports (JSON-RPC, MCP) use for each call — child scopes do not inherit the parent scope's scoped
/// instances. The framework ships a current-user initializer; hosts register additional ones (tenant,
/// correlation, …) with <c>TryAddEnumerable</c>, and every registered initializer runs via
/// <see cref="ServiceProviderDispatchScopeExtensions.CreateDispatchScope"/>.
/// </remarks>
public interface IDispatchScopeInitializer {
    /// <summary>Seeds scoped services in <paramref name="callScope"/> from <paramref name="context"/>.</summary>
    /// <param name="callScope">The freshly created per-call service provider.</param>
    /// <param name="context">The values captured at the dispatch boundary.</param>
    void Initialize(IServiceProvider callScope, DispatchScopeContext context);
}
