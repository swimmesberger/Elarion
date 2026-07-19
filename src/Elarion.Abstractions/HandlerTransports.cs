namespace Elarion.Abstractions;

/// <summary>
/// Selects which name-routed transports expose a <see cref="HandlerAttribute"/> handler. These transports share
/// a single operation name, so a handler declares (or infers) the name once and chooses its surfaces here. REST
/// is a separate opt-in via <c>[HttpEndpoint]</c> because it needs its own route, verb, and parameter binding.
/// </summary>
[Flags]
public enum HandlerTransports {
    /// <summary>Expose the handler over the JSON-RPC endpoint (<c>/rpc</c>).</summary>
    JsonRpc = 1,

    /// <summary>Expose the handler as an MCP tool.</summary>
    Mcp = 2,

    /// <summary>
    /// Expose the handler to bidirectional connection adapters (ADR-0053). Deliberately one flag for every
    /// adapter (SignalR, WebSocket, TCP, …): "callable over a live connection" is the semantic exposure
    /// decision; which framing carries it is not — an app that must distinguish adapters is hosting two
    /// conversations, not two flags.
    /// </summary>
    Connection = 4,

    /// <summary>Expose the handler over every name-routed transport. This is the default.</summary>
    All = JsonRpc | Mcp | Connection
}
