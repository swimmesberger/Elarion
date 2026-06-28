namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// Compile-time MCP metadata for a single registered JSON-RPC method, produced by the Elarion source
/// generator (<c>AppModuleDiscoveryGenerator</c>) — no runtime reflection or assembly scanning.
/// </summary>
/// <remarks>
/// This type is transport-neutral and carries no <c>ModelContextProtocol</c> SDK dependency; the ASP.NET MCP
/// adapter projects it onto SDK tool types.
/// </remarks>
public sealed record RpcMcpMethodMetadata {
    /// <summary>The JSON-RPC method name this metadata describes (e.g. <c>"clients.create"</c>).</summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// The request (params) type for this method, captured by the generator as <c>typeof(...)</c>. Used to build
    /// the tool input schema without consulting the dispatcher.
    /// </summary>
    public required Type RequestType { get; init; }

    /// <summary>
    /// Explicit MCP tool-name override from <c>[McpHandler(ToolName = ...)]</c>, or <see langword="null"/> to
    /// derive the tool name from <see cref="MethodName"/>.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>The tool description from a class-level <c>[Description]</c>, or <see langword="null"/> when absent.</summary>
    public string? Description { get; init; }

    /// <summary>Per-parameter descriptions sourced from <c>[Description]</c> on request members; empty when none.</summary>
    public IReadOnlyList<RpcMcpParameterDescriptor> Parameters { get; init; } = [];
}

/// <summary>A description for a single request property, keyed by its .NET property name.</summary>
/// <remarks>
/// The .NET property name (not the serialized JSON name) is captured at compile time. The JSON name is
/// resolved at startup from the live <see cref="System.Text.Json.JsonSerializerOptions.PropertyNamingPolicy"/>,
/// so the description attaches correctly regardless of the configured naming policy.
/// </remarks>
public readonly record struct RpcMcpParameterDescriptor(string PropertyName, string Description);

/// <summary>
/// Reflection-free lookup over the generated <see cref="RpcMcpMethodMetadata"/> table. The implementation is
/// produced by generated code (<c>configuration.GetMcpMetadata()</c>).
/// </summary>
public interface IRpcMcpMetadataSource {
    /// <summary>All method metadata, ordered by method name.</summary>
    IReadOnlyList<RpcMcpMethodMetadata> All { get; }

    /// <summary>Returns metadata for the given JSON-RPC method name, or <see langword="null"/> when not present.</summary>
    RpcMcpMethodMetadata? Get(string methodName);
}
