namespace Elarion.Abstractions;

/// <summary>
/// Optional MCP tool customization for a handler that is exposed on the MCP surface (i.e. its
/// <see cref="RpcMethodAttribute.Transports"/> includes <see cref="RpcTransports.Mcp"/>). The attribute is purely
/// additive: when absent, the handler is exposed as an MCP tool with a name derived from its RPC method name.
/// Tool/parameter descriptions come from <see cref="System.ComponentModel.DescriptionAttribute"/>, not from this
/// attribute. To keep a handler off the MCP surface, set <c>[RpcMethod(..., Transports = RpcTransports.JsonRpc)]</c>.
/// </summary>
/// <example>
/// <code>
/// [RpcMethod("clients.create")]
/// [McpMethod(ToolName = "create_client")]
/// public sealed class CreateClient : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpMethodAttribute : Attribute {
    /// <summary>
    /// Overrides the MCP tool name. When <see langword="null"/> or empty, the tool name is derived from the
    /// RPC method name via the host's configured tool-name transform (e.g. <c>"clients.create"</c> → <c>"clients_create"</c>).
    /// </summary>
    public string? ToolName { get; init; }
}
