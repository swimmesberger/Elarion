namespace Elarion.Abstractions;

/// <summary>
/// Marks a handler class as an RPC method, discoverable by the <c>RpcDispatcher</c>.
/// The handler must also implement <see cref="IHandler{TRequest, TResponse}"/>.
/// </summary>
/// <remarks>
/// The method name is declared once and shared by every dispatcher-based transport. Use
/// <see cref="Transports"/> to choose whether the handler is exposed over JSON-RPC, MCP, or both
/// (the default). REST is a separate opt-in via <c>[HttpEndpoint]</c>.
/// </remarks>
/// <example>
/// <code>
/// [RpcMethod("clients.create")]                                    // JSON-RPC + MCP (default)
/// public sealed class CreateClient(...) : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
///
/// [RpcMethod("clients.list", Transports = RpcTransports.JsonRpc)]  // JSON-RPC only
/// public sealed class ListClients(...) : IHandler&lt;ListClients.Query, Result&lt;ListClients.Response&gt;&gt; { ... }
///
/// [RpcMethod("ai.summarize", Transports = RpcTransports.Mcp)]      // MCP only
/// public sealed class Summarize(...) : IHandler&lt;Summarize.Command, Result&lt;Summarize.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RpcMethodAttribute(string methodName) : Attribute {
    /// <summary>The JSON-RPC method name (e.g., "clients.create").</summary>
    public string MethodName { get; } = methodName;

    /// <summary>
    /// The dispatcher-based transports that expose this handler. Defaults to
    /// <see cref="RpcTransports.All"/> (JSON-RPC and MCP).
    /// </summary>
    public RpcTransports Transports { get; init; } = RpcTransports.All;
}
