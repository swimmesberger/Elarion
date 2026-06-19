namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// Wraps the <see cref="JsonRpcDispatcher"/> that backs the MCP transport. It is registered as a distinct DI
/// singleton (separate from the JSON-RPC endpoint's dispatcher) so the two surfaces can expose different method
/// sets: an MCP-only handler is dispatchable here yet absent from <c>/rpc</c> and the JSON-RPC schema, while a
/// JSON-RPC-only handler is absent here. A handler exposed on both surfaces is registered in both dispatchers.
/// </summary>
/// <remarks>
/// The wrapper exists because both dispatchers are <see cref="JsonRpcDispatcher"/> instances and DI cannot
/// otherwise tell them apart. Created by <c>AddElarionMcp</c>; the MCP tool resolves it and dispatches via
/// <see cref="Inner"/>.
/// </remarks>
public sealed class McpDispatcher {
    /// <summary>Creates a wrapper over the dispatcher that contains the MCP-exposed methods.</summary>
    /// <param name="inner">The frozen dispatcher holding the MCP method registrations.</param>
    public McpDispatcher(JsonRpcDispatcher inner) {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The dispatcher containing the MCP-exposed methods.</summary>
    public JsonRpcDispatcher Inner { get; }
}
