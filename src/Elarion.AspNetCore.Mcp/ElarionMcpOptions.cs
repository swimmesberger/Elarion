namespace Elarion.AspNetCore.Mcp;

/// <summary>
/// Configuration for the Elarion MCP server. The server identity is application-supplied — no consuming-app
/// name is baked into the framework.
/// </summary>
public sealed class ElarionMcpOptions {
    /// <summary>The MCP server name reported to clients. Must be set by the consumer.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>The MCP server version reported to clients.</summary>
    public string ServerVersion { get; set; } = "1.0";

    /// <summary>The HTTP path the Streamable-HTTP MCP endpoint is mapped at. Defaults to <c>/mcp</c>.</summary>
    public string EndpointPath { get; set; } = "/mcp";

    /// <summary>
    /// Derives the MCP tool name from a JSON-RPC method name when no <c>[McpMethod(ToolName = ...)]</c> override
    /// is present. Defaults to replacing dots with underscores (e.g. <c>"clients.create"</c> → <c>"clients_create"</c>).
    /// </summary>
    public Func<string, string> ToolNameTransform { get; set; } = static methodName => methodName.Replace('.', '_');

    /// <summary>
    /// When <see langword="true"/> (default), failed tool calls include the JSON-RPC error code and data as
    /// structured content in addition to the error message.
    /// </summary>
    public bool IncludeErrorDetails { get; set; } = true;
}
