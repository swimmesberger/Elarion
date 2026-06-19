namespace Elarion.Abstractions;

/// <summary>
/// Selects which dispatcher-based transports expose an <see cref="RpcMethodAttribute"/> handler. These transports
/// share a single method name, so a handler declares the name once and chooses its surfaces here. REST is a
/// separate opt-in via <c>[HttpEndpoint]</c> because it needs its own route, verb, and parameter binding.
/// </summary>
[Flags]
public enum RpcTransports {
    /// <summary>Expose the handler over the JSON-RPC endpoint (<c>/rpc</c>).</summary>
    JsonRpc = 1,

    /// <summary>Expose the handler as an MCP tool.</summary>
    Mcp = 2,

    /// <summary>Expose the handler over every dispatcher-based transport. This is the default.</summary>
    All = JsonRpc | Mcp,
}
