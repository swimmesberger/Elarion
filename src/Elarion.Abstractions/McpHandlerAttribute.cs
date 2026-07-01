namespace Elarion.Abstractions;

/// <summary>
/// Optional MCP tool customization for a handler exposed on the MCP surface (i.e. its
/// <see cref="HandlerAttribute.Transports"/> includes <see cref="HandlerTransports.Mcp"/>). Purely additive:
/// when absent, the handler is exposed as an MCP tool with a name derived from its operation name.
/// Tool/parameter descriptions come from <see cref="System.ComponentModel.DescriptionAttribute"/>, not from
/// this attribute. To keep a handler off the MCP surface, set
/// <c>[Handler(..., Transports = HandlerTransports.JsonRpc)]</c>.
/// </summary>
/// <example>
/// <code>
/// [Handler("clients.create")]
/// [McpHandler(ToolName = "create_client")]
/// public sealed class CreateClient : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpHandlerAttribute : Attribute {
    /// <summary>
    /// Overrides the MCP tool name. When <see langword="null"/> or empty, the tool name is derived from the
    /// operation name via the host's configured tool-name transform (e.g. <c>"clients.create"</c> → <c>"clients_create"</c>).
    /// </summary>
    public string? ToolName { get; init; }
}
