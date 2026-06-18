using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.JsonRpc;

/// <summary>
/// Generates a JSON Schema document describing all registered JSON-RPC methods,
/// their request params, and response types. The output is used by frontend code
/// generators (e.g., TypeScript/Zod client stubs).
/// </summary>
public static class JsonRpcSchemaExporter {
    /// <summary>
    /// Generates a JSON schema string from the dispatcher's registered methods.
    /// </summary>
    /// <param name="dispatcher">A fully configured dispatcher with all methods registered.</param>
    /// <param name="jsonOptions">Optional serializer options (used for type info resolution).</param>
    /// <returns>A formatted JSON string containing the schema document.</returns>
    public static string Generate(JsonRpcDispatcher dispatcher, JsonSerializerOptions? jsonOptions = null) {
        var options = CreateSchemaOptions(jsonOptions);
        IReadOnlyList<(string MethodName, Type RequestType, Type ResponseType)> methods;
        try {
            methods = dispatcher.GetRegisteredMethods();
        } catch (InvalidOperationException ex) {
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher is not frozen. Call Freeze() after registering all JSON-RPC methods.",
                ex);
        }

        if (methods.Count == 0) {
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher has no registered methods.");
        }

        var methodsObj = new JsonObject();
        foreach (var (methodName, requestType, responseType) in methods) {
            var requestSchema = BuildSchemaNode(requestType, options, InjectReflectedDescription);
            var responseSchema = BuildSchemaNode(responseType, options, InjectReflectedDescription);

            methodsObj[methodName] = new JsonObject {
                ["params"] = requestSchema,
                ["result"] = responseSchema,
            };
        }

        var schema = new JsonObject {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "JSON-RPC 2.0 Schema",
            ["methods"] = methodsObj,
        };

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// .NET's schema exporter emits <c>["string","number"]</c> or <c>["string","integer"]</c>
    /// with a regex pattern for <c>decimal</c> and <c>int</c> types to indicate they can be
    /// read from either format. Since the API serialises all numerics as JSON numbers, this
    /// normalises those union types back to the plain numeric type (preserving nullability).
    /// </summary>
    /// <summary>
    /// Builds a JSON Schema node for <paramref name="type"/> using the shared exporter configuration
    /// (null-oblivious-as-non-nullable + numeric normalization), optionally composing an additional transform
    /// such as description injection. Single source of truth shared by the full schema export and the MCP
    /// input-schema builder, so the two cannot drift on null-handling or normalization.
    /// </summary>
    internal static JsonNode BuildSchemaNode(
        Type type,
        JsonSerializerOptions options,
        Func<JsonSchemaExporterContext, JsonNode, JsonNode>? extraTransform = null) {
        var exporterOptions = new JsonSchemaExporterOptions {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = extraTransform is null
                ? NormalizeNumericType
                : (ctx, schema) => extraTransform(ctx, NormalizeNumericType(ctx, schema)),
        };

        return options.GetJsonSchemaAsNode(type, exporterOptions);
    }

    /// <summary>
    /// Injects a <c>"description"</c> from a property's <see cref="DescriptionAttribute"/> into its schema node.
    /// Reads the attribute reflectively — appropriate for this build-time exporter (the runtime MCP path uses a
    /// generated, reflection-free table instead).
    /// </summary>
    private static JsonNode InjectReflectedDescription(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.PropertyInfo?.AttributeProvider is { } provider &&
            provider.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                is [DescriptionAttribute { Description.Length: > 0 } description, ..] &&
            schema is JsonObject obj) {
            obj["description"] = description.Description;
        }

        return schema;
    }

    internal static JsonNode NormalizeNumericType(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (schema is not JsonObject obj ||
            obj["type"] is not JsonArray typeArr ||
            !obj.ContainsKey("pattern")) {
            return schema;
        }

        var types = typeArr
            .Select(t => t?.GetValue<string>())
            .Where(t => t is not null)
            .ToList()!;

        var numericType = types.FirstOrDefault(t => t is "number" or "integer");
        if (numericType is null || !types.Contains("string")) {
            return schema;
        }

        obj.Remove("pattern");
        var remaining = types.Where(t => t != "string").ToList();
        obj["type"] = remaining.Count == 1
            ? (JsonNode)remaining[0]!
            : new JsonArray(remaining.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());

        return schema;
    }

    internal static JsonSerializerOptions CreateSchemaOptions(JsonSerializerOptions? jsonOptions) {
        var options = jsonOptions is null
            ? new JsonSerializerOptions {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() },
            }
            : new JsonSerializerOptions(jsonOptions);

        if (options.TypeInfoResolver is null && options.TypeInfoResolverChain.Count == 0) {
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }

        return options;
    }
}
