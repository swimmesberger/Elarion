using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Shared emission for the reflection-free MCP metadata table (<c>RpcMcpMethodMetadata[]</c>), consumed by
/// <see cref="AppModuleDiscoveryGenerator"/> (per-module <c>Get{Module}McpMetadata</c> + the gated
/// <c>GetMcpMetadata</c> aggregate). Only entries whose <see cref="RpcMethodEmission.Model.OnMcp"/> is set
/// are emitted.
/// </summary>
internal static class McpMetadataEmission
{
    public const string Ns = "global::Elarion.JsonRpc.Mcp";

    /// <summary>Returns whether any entry is exposed on the MCP surface.</summary>
    public static bool AnyMcp(IEnumerable<RpcMethodEmission.Model> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.OnMcp)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Emits <c>RpcMcpMethodMetadata</c> object-initializer elements (each with a trailing comma) for the
    /// MCP-surfaced entries, indented by <paramref name="indent"/>.
    /// </summary>
    public static void AppendElements(StringBuilder sb, IEnumerable<RpcMethodEmission.Model> entries, string indent)
    {
        foreach (var entry in entries)
        {
            if (!entry.OnMcp)
                continue;

            sb.AppendLine($"{indent}new {Ns}.RpcMcpMethodMetadata");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    MethodName = {Literal(entry.MethodName)},");
            sb.AppendLine($"{indent}    RequestType = typeof({entry.RequestTypeFqn}),");
            if (entry.ToolName is not null)
                sb.AppendLine($"{indent}    ToolName = {Literal(entry.ToolName)},");
            if (entry.Description is not null)
                sb.AppendLine($"{indent}    Description = {Literal(entry.Description)},");
            if (entry.Parameters.Count > 0)
            {
                sb.AppendLine($"{indent}    Parameters = new {Ns}.RpcMcpParameterDescriptor[]");
                sb.AppendLine($"{indent}    {{");
                foreach (var parameter in entry.Parameters)
                {
                    sb.AppendLine(
                        $"{indent}        new {Ns}.RpcMcpParameterDescriptor({Literal(parameter.PropertyName)}, {Literal(parameter.Description)}),");
                }

                sb.AppendLine($"{indent}    }},");
            }

            sb.AppendLine($"{indent}}},");
        }
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
