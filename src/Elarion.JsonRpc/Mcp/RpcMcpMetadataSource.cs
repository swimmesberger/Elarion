using System.Collections.Frozen;

namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// Frozen-dictionary-backed <see cref="IRpcMcpMetadataSource"/>. Instantiated by generated code
/// (<c>RpcMethodMap.McpMetadata()</c>) with the compile-time metadata table.
/// </summary>
public sealed class RpcMcpMetadataSource : IRpcMcpMetadataSource {
    private readonly FrozenDictionary<string, RpcMcpMethodMetadata> _byMethodName;

    /// <summary>Creates a metadata source over the given method metadata.</summary>
    /// <param name="methods">The full set of method metadata; exposed in order via <see cref="All"/>.</param>
    public RpcMcpMetadataSource(IReadOnlyList<RpcMcpMethodMetadata> methods) {
        All = methods;
        // OrdinalIgnoreCase mirrors JsonRpcDispatcher's method-name comparison so lookups stay consistent.
        _byMethodName = methods.ToFrozenDictionary(static m => m.MethodName, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<RpcMcpMethodMetadata> All { get; }

    /// <inheritdoc />
    public RpcMcpMethodMetadata? Get(string methodName) =>
        _byMethodName.TryGetValue(methodName, out var metadata) ? metadata : null;
}
