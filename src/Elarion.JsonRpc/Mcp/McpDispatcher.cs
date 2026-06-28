using System.Text.Json;
using Elarion.Abstractions.Dispatch;

namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// The MCP transport adapter over the shared <see cref="HandlerDispatcher"/> (the named bus). It exposes only the
/// operations flagged <see cref="HandlerTransports.Mcp"/>, so an MCP-only handler is dispatchable here yet absent
/// from <c>/rpc</c> and the JSON-RPC schema, a JSON-RPC-only handler is absent here, and a "both" handler is
/// reachable from either surface — all from one registry.
/// </summary>
/// <remarks>
/// Registered as a distinct DI singleton by <c>AddElarionMcp</c>; the MCP tool resolves it and dispatches via
/// <see cref="RpcToolInvoker"/> filtered by <see cref="HandlerTransports.Mcp"/>. It carries the
/// <see cref="JsonSerializerOptions"/> used to (de)serialize tool arguments and results.
/// </remarks>
public sealed class McpDispatcher {
    /// <summary>Creates the MCP adapter over the shared registry.</summary>
    /// <param name="inner">The shared handler dispatcher (the named bus).</param>
    /// <param name="serializerOptions">The options used to (de)serialize MCP tool arguments and results.</param>
    public McpDispatcher(HandlerDispatcher inner, JsonSerializerOptions serializerOptions) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        Inner = inner;
        SerializerOptions = serializerOptions;
    }

    /// <summary>The shared registry; MCP serves the subset flagged <see cref="HandlerTransports.Mcp"/>.</summary>
    public HandlerDispatcher Inner { get; }

    /// <summary>The JSON options used to (de)serialize MCP tool arguments and results.</summary>
    public JsonSerializerOptions SerializerOptions { get; }
}
