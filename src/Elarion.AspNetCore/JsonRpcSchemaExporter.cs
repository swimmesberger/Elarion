using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.AspNetCore;

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

        var exporterOptions = new JsonSchemaExporterOptions {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = NormalizeNumericType,
        };

        var methodsObj = new JsonObject();
        foreach (var (methodName, requestType, responseType) in methods) {
            var requestSchema = options.GetJsonSchemaAsNode(requestType, exporterOptions);
            var responseSchema = options.GetJsonSchemaAsNode(responseType, exporterOptions);

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
    private static JsonNode NormalizeNumericType(JsonSchemaExporterContext ctx, JsonNode schema) {
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

    private static JsonSerializerOptions CreateSchemaOptions(JsonSerializerOptions? jsonOptions) {
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
