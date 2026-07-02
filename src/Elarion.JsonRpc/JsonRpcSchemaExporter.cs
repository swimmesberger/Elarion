using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    // JSON Schema export has no System.Text.Json source-gen equivalent — JsonSchemaExporter walks type metadata
    // reflectively — so this whole surface is build-time-only and inherently reflection/dynamic-code dependent.
    // The requirement is declared honestly here and flows to callers (the build-time schema tool, MCP tool-schema
    // registration) rather than being silently suppressed.
    internal const string SchemaReflectionMessage =
        "JSON Schema export reflects over the request/response types; it is a build-time operation and is not supported under trimming or Native AOT.";

    /// <summary>
    /// Generates a JSON schema string from the dispatcher's registered methods.
    /// </summary>
    /// <param name="dispatcher">A fully configured dispatcher with all methods registered.</param>
    /// <param name="jsonOptions">Optional serializer options (used for type info resolution).</param>
    /// <returns>A formatted JSON string containing the schema document.</returns>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    public static string Generate(JsonRpcDispatcher dispatcher, JsonSerializerOptions? jsonOptions = null) {
        var options = CreateSchemaOptions(jsonOptions);
        IReadOnlyList<(string MethodName, Type RequestType, Type ResponseType, bool Idempotent)> methods;
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
        foreach (var (methodName, requestType, responseType, idempotent) in methods) {
            var requestSchema = BuildSchemaNode(requestType, options, InjectReflectedAnnotations);
            var responseSchema = BuildSchemaNode(responseType, options, InjectReflectedAnnotations);

            var method = new JsonObject {
                ["params"] = requestSchema,
                ["result"] = responseSchema,
            };
            // Only emit the flag when set, so the schema stays byte-identical for non-idempotent methods.
            if (idempotent) {
                method["idempotent"] = true;
            }

            methodsObj[methodName] = method;
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
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
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
    /// Composes the reflective per-property transforms applied to the full schema export: the
    /// <see cref="DescriptionAttribute"/> description plus the DataAnnotations constraint keywords.
    /// </summary>
    private static JsonNode InjectReflectedAnnotations(JsonSchemaExporterContext ctx, JsonNode schema) =>
        InjectReflectedConstraints(ctx, InjectReflectedDescription(ctx, schema));

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

    /// <summary>
    /// Injects JSON Schema validation keywords from a property's
    /// <c>System.ComponentModel.DataAnnotations</c> attributes, mirroring
    /// <c>Microsoft.AspNetCore.OpenApi</c>'s attribute→keyword mapping (plus
    /// <see cref="EmailAddressAttribute"/> → <c>format: "email"</c>) so the JSON-RPC schema, the MCP tool input
    /// schemas, and the OpenAPI document agree on the same declared constraints (ADR-0027). Attributes apply in
    /// declaration order with last-wins on duplicate keywords, matching Microsoft. Reads the attributes
    /// reflectively — appropriate for this build-time exporter.
    /// </summary>
    internal static JsonNode InjectReflectedConstraints(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.PropertyInfo?.AttributeProvider is not { } provider || schema is not JsonObject obj) {
            return schema;
        }

        // Type patterns intentionally match subclasses: a reusable custom constraint deriving a mapped
        // attribute (e.g. a [Slug] : RegularExpressionAttribute) reaches every schema surface for free.
        foreach (var attribute in provider.GetCustomAttributes(inherit: false)) {
            switch (attribute) {
                case RangeAttribute range:
                    ApplyRange(obj, range);
                    break;
                case MinLengthAttribute minLength:
                    obj[IsArraySchema(obj) ? "minItems" : "minLength"] = minLength.Length;
                    break;
                case MaxLengthAttribute maxLength:
                    obj[IsArraySchema(obj) ? "maxItems" : "maxLength"] = maxLength.Length;
                    break;
                case LengthAttribute length: {
                    var isArray = IsArraySchema(obj);
                    obj[isArray ? "minItems" : "minLength"] = length.MinimumLength;
                    obj[isArray ? "maxItems" : "maxLength"] = length.MaximumLength;
                    break;
                }
                case StringLengthAttribute stringLength:
                    if (stringLength.MinimumLength > 0) {
                        obj["minLength"] = stringLength.MinimumLength;
                    }

                    obj["maxLength"] = stringLength.MaximumLength;
                    break;
                case RegularExpressionAttribute regularExpression:
                    obj["pattern"] = regularExpression.Pattern;
                    break;
                case UrlAttribute:
                    obj["format"] = "uri";
                    break;
                case EmailAddressAttribute:
                    obj["format"] = "email";
                    break;
                case Base64StringAttribute:
                    obj["format"] = "byte";
                    break;
            }
        }

        return schema;
    }

    private static void ApplyRange(JsonObject schema, RangeAttribute range) {
        if (TryConvertRangeOperand(range.Minimum, out var minimum)) {
            schema[range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum"] = minimum;
        }

        if (TryConvertRangeOperand(range.Maximum, out var maximum)) {
            schema[range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum"] = maximum;
        }
    }

    /// <summary>
    /// Normalizes a <see cref="RangeAttribute"/> operand — <c>int</c>/<c>double</c> from the numeric constructors
    /// or a string from the <c>Range(typeof(decimal), "…", "…")</c> form — to a decimal emitted as a JSON number.
    /// Parses with the invariant culture and skips a bound that does not fit a decimal (e.g.
    /// <see cref="double.MaxValue"/>), the same tolerance Microsoft's OpenAPI mapping applies.
    /// </summary>
    private static bool TryConvertRangeOperand(object? operand, out decimal value) {
        switch (operand) {
            case int intValue:
                value = intValue;
                return true;
            case double doubleValue:
                return decimal.TryParse(
                    doubleValue.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value);
            case string stringValue:
                return decimal.TryParse(stringValue, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// Whether a schema node's <c>type</c> is (or, for a nullable union like <c>["array","null"]</c>, contains)
    /// <c>"array"</c> — the switch between <c>minLength</c>/<c>maxLength</c> and <c>minItems</c>/<c>maxItems</c>.
    /// </summary>
    private static bool IsArraySchema(JsonObject schema) =>
        schema["type"] switch {
            JsonValue single => single.TryGetValue<string>(out var type) && type == "array",
            JsonArray union => union.Any(static node =>
                node is JsonValue value && value.TryGetValue<string>(out var type) && type == "array"),
            _ => false,
        };

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

    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
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
