namespace Elarion.JsonRpc;

/// <summary>
/// A hook that seeds scoped services in a per-call dispatch scope. Implementations are resolved from the
/// call scope and run once per dispatch, immediately after the scope is created and before the handler runs.
/// </summary>
/// <remarks>
/// This is the extension point for carrying request-boundary state into the fresh DI scope dispatcher-based
/// transports (JSON-RPC, MCP) use for each call — child scopes do not inherit the parent scope's scoped
/// instances. An initializer can seed from two sources: the captured <see cref="DispatchScopeContext"/>
/// values, or <paramref name="inheritFrom"/> — the originating request scope, when one exists — which lets it
/// <em>copy</em> an already-built instance instead of rebuilding it (see <see cref="CopyingDispatchScopeInitializer{T}"/>).
/// The framework ships a current-user initializer; hosts register additional ones (tenant, correlation, …)
/// with <c>TryAddEnumerable</c>, and every registered initializer runs via
/// <see cref="ServiceProviderDispatchScopeExtensions.CreateDispatchScope"/>.
/// </remarks>
public interface IDispatchScopeInitializer {
    /// <summary>Seeds scoped services in <paramref name="callScope"/>.</summary>
    /// <param name="callScope">The freshly created per-call service provider.</param>
    /// <param name="inheritFrom">
    /// The originating request scope to copy already-built scoped instances from, or <see langword="null"/>
    /// when there is none (e.g. MCP, which dispatches from the application root). Never resolve scoped
    /// services from the application root; rely on <paramref name="context"/> instead when this is null.
    /// </param>
    /// <param name="context">The values captured at the dispatch boundary.</param>
    void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context);
}
