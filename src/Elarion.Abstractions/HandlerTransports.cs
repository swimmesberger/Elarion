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

    /// <summary>Expose the handler over every name-routed transport. This is the default.</summary>
    All = JsonRpc | Mcp,
}
