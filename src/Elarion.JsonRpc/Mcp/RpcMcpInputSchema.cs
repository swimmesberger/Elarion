using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.JsonRpc.Mcp;

/// <summary>
/// Builds the JSON Schema for an MCP tool's input from a JSON-RPC request type, reusing the JSON-RPC schema
/// exporter's numeric normalization and injecting compile-time parameter descriptions.
/// </summary>
public static class RpcMcpInputSchema {
    /// <summary>
    /// Generates the input-schema <see cref="JsonElement"/> for <paramref name="requestType"/> using
    /// <paramref name="jsonOptions"/> (for property naming + type resolution), injecting the supplied
    /// parameter descriptions.
    /// </summary>
    /// <remarks>
    /// Descriptions are matched on the .NET property name via the schema node's
    /// <see cref="JsonSchemaExporterContext.PropertyInfo"/> attribute provider, so they attach correctly
    /// regardless of the configured <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> — no JSON-name
    /// guessing at compile time. The returned element owns its memory independently of the transient schema node.
    /// </remarks>
    public static JsonElement Build(
        Type requestType,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<RpcMcpParameterDescriptor> parameterDescriptions) {
        var options = JsonRpcSchemaExporter.CreateSchemaOptions(jsonOptions);

        Dictionary<string, string>? descriptionsByPropertyName = null;
        if (parameterDescriptions.Count > 0) {
            descriptionsByPropertyName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var descriptor in parameterDescriptions) {
                descriptionsByPropertyName[descriptor.PropertyName] = descriptor.Description;
            }
        }

        var exporterOptions = new JsonSchemaExporterOptions {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = descriptionsByPropertyName is null
                ? JsonRpcSchemaExporter.NormalizeNumericType
                : (ctx, schema) => InjectDescription(
                    ctx, JsonRpcSchemaExporter.NormalizeNumericType(ctx, schema), descriptionsByPropertyName),
        };

        var schemaNode = options.GetJsonSchemaAsNode(requestType, exporterOptions);

        // Parse + clone so the returned element owns its memory independently of the transient JsonNode.
        using var document = JsonDocument.Parse(schemaNode.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode InjectDescription(
        JsonSchemaExporterContext ctx,
        JsonNode schema,
        Dictionary<string, string> descriptionsByPropertyName) {
        if (ctx.PropertyInfo?.AttributeProvider is MemberInfo member &&
            descriptionsByPropertyName.TryGetValue(member.Name, out var description) &&
            schema is JsonObject obj) {
            obj["description"] = description;
        }

        return schema;
    }
}
