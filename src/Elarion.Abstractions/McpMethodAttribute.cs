namespace Elarion.Abstractions;

/// <summary>
/// Optional per-handler MCP tool metadata for a handler already marked with <see cref="RpcMethodAttribute"/>.
/// The attribute is purely additive: an absent attribute means the handler is exposed as an MCP tool with a
/// name derived from its RPC method name. Tool/parameter descriptions come from
/// <see cref="System.ComponentModel.DescriptionAttribute"/>, not from this attribute.
/// </summary>
/// <example>
/// <code>
/// [RpcMethod("clients.create")]
/// [McpMethod(ToolName = "create_client")]
/// public sealed class CreateClient : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
///
/// // Keep a method on JSON-RPC but off the MCP surface:
/// [RpcMethod("admin.purge"), McpMethod(Enabled = false)]
/// public sealed class PurgeEverything : IHandler&lt;...&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class McpMethodAttribute : Attribute {
    /// <summary>
    /// Overrides the MCP tool name. When <see langword="null"/> or empty, the tool name is derived from the
    /// RPC method name via the host's configured tool-name transform (e.g. <c>"clients.create"</c> → <c>"clients_create"</c>).
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Whether this handler is exposed as an MCP tool. Defaults to <see langword="true"/>; set to
    /// <see langword="false"/> to keep the method available over JSON-RPC but excluded from the MCP surface.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
