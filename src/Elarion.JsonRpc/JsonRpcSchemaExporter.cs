using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Elarion.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.JsonRpc;

/// <summary>Whether a schema describes data the server reads (a request) or writes (a response, event or error payload).</summary>
internal enum SchemaDirection {
    /// <summary>Data the server reads; members with a default value are optional to send.</summary>
    Request,

    /// <summary>Data the server writes; every non-nullable member is always written.</summary>
    Response
}

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
    /// <param name="exportOptions">
    /// Optional capability vocabulary (modules/features, permissions, roles) emitted as the schema's
    /// <c>capabilities</c> block; omitted entirely when absent so existing schemas stay byte-identical.
    /// </param>
    /// <returns>A formatted JSON string containing the schema document.</returns>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    public static string Generate(
        JsonRpcDispatcher dispatcher,
        JsonSerializerOptions? jsonOptions = null,
        JsonRpcSchemaExportOptions? exportOptions = null) {
        var options = CreateSchemaOptions(jsonOptions);
        IReadOnlyList<(string MethodName, Type RequestType, Type ResponseType, bool Idempotent,
            IReadOnlyList<ErrorContract> Errors)> methods;
        try {
            methods = dispatcher.GetRegisteredMethods();
        }
        catch (InvalidOperationException ex) {
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher is not frozen. Call Freeze() after registering all JSON-RPC methods.",
                ex);
        }

        if (methods.Count == 0)
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher has no registered methods.");

        var methodsObj = new JsonObject();
        foreach (var (methodName, requestType, responseType, idempotent, errors) in methods) {
            var requestSchema = BuildSchemaNode(
                requestType, options, SchemaDirection.Request, InjectReflectedAnnotations);
            var responseSchema = BuildSchemaNode(
                responseType, options, SchemaDirection.Response, InjectReflectedAnnotations);

            var method = new JsonObject {
                ["params"] = requestSchema,
                ["result"] = responseSchema
            };
            // Only emit the flag when set, so the schema stays byte-identical for non-idempotent methods.
            if (idempotent) method["idempotent"] = true;

            // The declared error contract (ADR-0080): code -> kind + optional payload schema, in ordinal code
            // order. Omitted when the operation declares none, so contract-free schemas stay byte-identical.
            if (BuildErrorsNode(errors, options) is { } errorsNode) method["errors"] = errorsNode;

            methodsObj[methodName] = method;
        }

        var schema = new JsonObject {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "JSON-RPC 2.0 Schema",
            ["methods"] = methodsObj
        };

        var capabilities = BuildCapabilitiesNode(exportOptions);
        if (capabilities is not null) schema["capabilities"] = capabilities;

        var events = BuildEventsNode(exportOptions, options);
        if (events is not null) schema["events"] = events;

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Builds a method's <c>errors</c> object — each declared error code with its <c>kind</c> (the kind's default
    /// code, e.g. <c>not_found</c>) and, when the error carries a typed payload, its <c>data</c> schema — or
    /// <see langword="null"/> when the method declares none.
    /// </summary>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonObject? BuildErrorsNode(IReadOnlyList<ErrorContract> errors, JsonSerializerOptions options) {
        if (errors.Count == 0) return null;

        var node = new JsonObject();
        foreach (var error in errors.OrderBy(static e => e.Code, StringComparer.Ordinal)) {
            var entry = new JsonObject { ["kind"] = ErrorCodes.ForKind(error.Kind) };
            if (error.DataType is { } dataType)
                entry["data"] = BuildSchemaNode(dataType, options, SchemaDirection.Response, InjectReflectedAnnotations);

            node[error.Code] = entry;
        }

        return node;
    }

    /// <summary>
    /// Builds the <c>events</c> block — each declared client-event topic with its payload schema (ADR-0043),
    /// in deterministic ordinal topic order — or <see langword="null"/> when no topics were supplied, keeping
    /// event-free schemas byte-identical.
    /// </summary>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonObject? BuildEventsNode(
        JsonRpcSchemaExportOptions? exportOptions, JsonSerializerOptions options) {
        if (exportOptions?.ClientEventTopics is not { Topics.Count: > 0 } manifest) return null;

        var events = new JsonObject();
        foreach (var topic in manifest.Topics.OrderBy(static t => t.Name, StringComparer.Ordinal))
            events[topic.Name] = new JsonObject {
                ["payload"] = BuildSchemaNode(
                    topic.EventType, options, SchemaDirection.Response, InjectReflectedAnnotations)
            };

        return events;
    }

    /// <summary>
    /// Builds the <c>capabilities</c> vocabulary block — module names with their client-exposed feature flags
    /// (enabled modules only, matching the method gating), the structured permission catalog, and the role names —
    /// or <see langword="null"/> when nothing was supplied, keeping vocabulary-free schemas byte-identical. All
    /// collections are emitted in a deterministic ordinal order.
    /// </summary>
    private static JsonObject? BuildCapabilitiesNode(JsonRpcSchemaExportOptions? exportOptions) {
        if (exportOptions is null) return null;

        var capabilities = new JsonObject();

        if (exportOptions.ClientCapabilities is { Modules.Count: > 0 } manifest) {
            var modules = new JsonObject();
            foreach (var module in manifest.Modules
                         .Where(static m => m.Enabled)
                         .OrderBy(static m => m.Name, StringComparer.Ordinal)) {
                var features = new JsonArray();
                var exposed = (exportOptions.FeatureFlags?.All ?? [])
                    .Where(f => f.ExposeToClient && string.Equals(f.Module, module.Name, StringComparison.Ordinal))
                    .Select(static f => f.Name)
                    .OrderBy(static name => name, StringComparer.Ordinal);
                foreach (var feature in exposed)
                    features.Add((JsonNode?)JsonValue.Create(feature));

                modules[module.Name] = new JsonObject { ["features"] = features };
            }

            if (modules.Count > 0) capabilities["modules"] = modules;
        }

        if (exportOptions.PermissionCatalog is { } catalog) {
            if (catalog.Modules.Count > 0) {
                // Structured entries (not just the composed string) so client generators can nest by resource/verb
                // without re-parsing the composed value, whose parts are free-form strings.
                var permissions = new JsonArray();
                foreach (var entry in catalog.Modules
                             .SelectMany(static m => m.Permissions)
                             .DistinctBy(static e => e.Permission)
                             .OrderBy(static e => e.Permission, StringComparer.Ordinal))
                    permissions.Add((JsonNode)new JsonObject {
                        ["permission"] = entry.Permission,
                        ["resource"] = entry.Resource,
                        ["verb"] = entry.Verb
                    });

                if (permissions.Count > 0) capabilities["permissions"] = permissions;
            }

            if (catalog.Roles.Count > 0) {
                var roles = new JsonArray();
                foreach (var role in catalog.Roles.OrderBy(static r => r, StringComparer.Ordinal))
                    roles.Add((JsonNode?)JsonValue.Create(role));

                capabilities["roles"] = roles;
            }
        }

        return capabilities.Count > 0 ? capabilities : null;
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
        SchemaDirection direction,
        Func<JsonSchemaExporterContext, JsonNode, JsonNode>? extraTransform = null) {
        var exporterOptions = new JsonSchemaExporterOptions {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (ctx, schema) => {
                var node = ApplyRequiredness(ctx, NormalizeNumericType(ctx, MapFilePayload(ctx, schema)), direction);
                return extraTransform is null ? node : extraTransform(ctx, node);
            }
        };

        if (direction == SchemaDirection.Response && options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingDefault)
            throw new InvalidOperationException(
                "Cannot export a response schema: the serializer options use DefaultIgnoreCondition = " +
                "WhenWritingDefault, which omits non-nullable members holding their default value while the " +
                "schema marks them required. Use WhenWritingNull (the Elarion default) or Never.");

        return options.GetJsonSchemaAsNode(type, exporterOptions);
    }

    /// <summary>
    /// The single requiredness rule of the wire contract (ADR-0082), applied to every object node: a non-nullable
    /// property is <c>required</c>; a nullable property is optional. For <see cref="SchemaDirection.Request"/>
    /// types a non-nullable property that has a default value — a constructor parameter default, or an initializer
    /// that yields a non-default value — is also optional to send. This replaces the serializer's own
    /// <c>required</c> list (C# <c>required</c> / <c>[JsonRequired]</c>), so nullability is the one source of truth
    /// in both directions.
    /// </summary>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonNode ApplyRequiredness(JsonSchemaExporterContext ctx, JsonNode schema, SchemaDirection direction) {
        if (ctx.TypeInfo.Kind != JsonTypeInfoKind.Object
            || schema is not JsonObject obj
            || obj["properties"] is not JsonObject properties)
            return schema;

        obj.Remove("required");

        object? probe = null;
        var probed = false;
        var required = new JsonArray();
        foreach (var property in ctx.TypeInfo.Properties) {
            if (!properties.TryGetPropertyValue(property.Name, out var propertySchema)) continue;

            if (direction == SchemaDirection.Response)
                RejectConditionalWrite(ctx.TypeInfo.Type, property, propertySchema, options: ctx.TypeInfo.Options);

            if (AllowsNull(propertySchema, property, direction)) continue;

            if (direction == SchemaDirection.Request && HasDefaultValue(ctx.TypeInfo, property, ref probe, ref probed))
                continue;

            required.Add((JsonNode?)JsonValue.Create(property.Name));
        }

        if (required.Count > 0) obj["required"] = required;

        return schema;
    }

    private static bool AllowsNull(JsonNode? propertySchema, JsonPropertyInfo property, SchemaDirection direction) {
        switch (propertySchema) {
            case JsonObject node when node.Count == 0:
            case JsonValue:
                // An unconstrained schema ({} / true) says nothing about null; fall back to the CLR annotation.
                return direction == SchemaDirection.Request ? property.IsSetNullable : property.IsGetNullable;
            case JsonObject node:
                // A nullable enum under a string-enum converter is exported as a bare value list with no "type"
                // keyword ({"enum":["A","B",null]}), so null membership must be read from the enum itself.
                return TypeUnionContainsNull(node["type"])
                       || EnumContainsNull(node["enum"])
                       || AnyOfContainsNull(node["anyOf"])
                       || AnyOfContainsNull(node["oneOf"]);
            default:
                return false;
        }
    }

    private static bool TypeUnionContainsNull(JsonNode? type) {
        return type switch {
            JsonValue single => single.TryGetValue<string>(out var name) && name == "null",
            JsonArray union => union.Any(static item =>
                item is JsonValue value && value.TryGetValue<string>(out var name) && name == "null"),
            _ => false
        };
    }

    private static bool EnumContainsNull(JsonNode? values) {
        return values is JsonArray array && array.Any(static item => item is null);
    }

    private static bool AnyOfContainsNull(JsonNode? alternatives) {
        return alternatives is JsonArray array
               && array.Any(static item => item is JsonObject node
                                           && (TypeUnionContainsNull(node["type"]) || EnumContainsNull(node["enum"])));
    }

    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static bool HasDefaultValue(
        JsonTypeInfo typeInfo, JsonPropertyInfo property, ref object? probe, ref bool probed) {
        if (property.AssociatedParameter is { HasDefaultValue: true }) return true;

        // An initializer is only observable on an instance: construct one once per type and compare the member
        // with the CLR default. An initializer that equals the CLR default (= 0, = false) is indistinguishable
        // from none — declare the member nullable or give it a non-default value.
        if (property.Get is null || property.AssociatedParameter is not null || typeInfo.CreateObject is not { } create)
            return false;

        if (!probed) {
            probed = true;
            try {
                probe = create();
            }
            catch (Exception) {
                probe = null;
            }
        }

        if (probe is null) return false;

        try {
            var value = property.Get(probe);
            return value is not null
                   && !(property.PropertyType.IsValueType && value.Equals(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(property.PropertyType)));
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// A response member that the serializer may omit is an optional member, but the wire contract marks every
    /// non-nullable member required (ADR-0082) — so a conditional write on a non-nullable member is a contract
    /// violation, reported here for types the compile-time check cannot see (client events, custom contexts).
    /// </summary>
    private static void RejectConditionalWrite(
        Type declaringType, JsonPropertyInfo property, JsonNode? propertySchema, JsonSerializerOptions options) {
        if (AllowsNull(propertySchema, property, SchemaDirection.Response)) return;

        if (property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), true)
                is not [JsonIgnoreAttribute { Condition: var condition }, ..])
            return;

        if (condition == JsonIgnoreCondition.WhenWritingDefault
            || (condition == JsonIgnoreCondition.WhenWritingNull && !property.PropertyType.IsValueType))
            throw new InvalidOperationException(
                $"Cannot export the schema of '{declaringType}': member '{property.Name}' is non-nullable but is " +
                $"annotated [JsonIgnore(Condition = {condition})]. Non-nullable members are required on the wire, " +
                "so the serializer must always write them. Make the member nullable to declare it optional, or " +
                "remove the ignore condition.");
    }

    /// <summary>
    /// Replaces the schema node for <see cref="Elarion.Abstractions.ElarionFile"/> — opaque to the exporter because the type
    /// serializes through its custom converter — with the converter's fixed base64 envelope, wherever the type
    /// appears (a method's whole result, or a property inside a params/result DTO for uploads). <c>data</c>
    /// carries <c>format: "byte"</c> like <c>[Base64String]</c> properties, and the object is marked
    /// <c>x-elarion-file: true</c> so the TypeScript client generator maps it to a native <c>File</c> instead
    /// of a plain envelope interface.
    /// </summary>
    private static JsonNode MapFilePayload(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.TypeInfo.Type != typeof(Abstractions.ElarionFile)) return schema;

        return new JsonObject {
            ["type"] = "object",
            ["x-elarion-file"] = true,
            ["description"] = "A binary file payload; data is the base64-encoded content.",
            ["properties"] = new JsonObject {
                ["contentType"] = new JsonObject { ["type"] = "string" },
                ["fileName"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject { ["type"] = "string", ["format"] = "byte" }
            },
            ["required"] = new JsonArray("contentType", "data")
        };
    }

    /// <summary>
    /// Composes the reflective per-property transforms applied to the full schema export: the
    /// <see cref="DescriptionAttribute"/> description plus the DataAnnotations constraint keywords.
    /// </summary>
    private static JsonNode InjectReflectedAnnotations(JsonSchemaExporterContext ctx, JsonNode schema) {
        return InjectReflectedConstraints(ctx, InjectReflectedDescription(ctx, schema));
    }

    /// <summary>
    /// Injects a <c>"description"</c> from a property's <see cref="DescriptionAttribute"/> into its schema node.
    /// Reads the attribute reflectively — appropriate for this build-time exporter (the runtime MCP path uses a
    /// generated, reflection-free table instead).
    /// </summary>
    private static JsonNode InjectReflectedDescription(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.PropertyInfo?.AttributeProvider is { } provider &&
            provider.GetCustomAttributes(typeof(DescriptionAttribute), false)
                is [DescriptionAttribute { Description.Length: > 0 } description, ..] &&
            schema is JsonObject obj)
            obj["description"] = description.Description;

        return schema;
    }

    /// <summary>
    /// Injects JSON Schema validation keywords from a property's
    /// <c>System.ComponentModel.DataAnnotations</c> attributes, mirroring
    /// <c>Microsoft.AspNetCore.OpenApi</c>'s attribute→keyword mapping (plus
    /// <see cref="EmailAddressAttribute"/> → <c>format: "email"</c> and
    /// <see cref="AllowedValuesAttribute"/> → <c>enum</c>) so the JSON-RPC schema, the MCP tool input
    /// schemas, and the OpenAPI document agree on the same declared constraints (ADR-0027). Attributes apply in
    /// declaration order with last-wins on duplicate keywords, matching Microsoft. Reads the attributes
    /// reflectively — appropriate for this build-time exporter.
    /// </summary>
    internal static JsonNode InjectReflectedConstraints(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.PropertyInfo?.AttributeProvider is not { } provider || schema is not JsonObject obj) return schema;

        // Type patterns intentionally match subclasses: a reusable custom constraint deriving a mapped
        // attribute (e.g. a [Slug] : RegularExpressionAttribute) reaches every schema surface for free.
        foreach (var attribute in provider.GetCustomAttributes(false))
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
                    if (stringLength.MinimumLength > 0) obj["minLength"] = stringLength.MinimumLength;

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
                case AllowedValuesAttribute allowedValues:
                    obj["enum"] = CreateEnumArray(allowedValues.Values);
                    break;
            }

        return schema;
    }

    /// <summary>
    /// Converts <see cref="AllowedValuesAttribute"/> values — compile-time constants: strings, numbers,
    /// booleans, or <c>null</c> — to a JSON Schema <c>enum</c> array, preserving declaration order. The
    /// generated TypeScript client maps the keyword to <c>z.enum</c>/literal unions, so a declared value set
    /// (e.g. a configuration-variant vocabulary) pre-validates client-side like every other constraint.
    /// </summary>
    private static JsonArray CreateEnumArray(IReadOnlyList<object?> values) {
        var array = new JsonArray();
        foreach (var value in values) {
            // The statically-typed local keeps overload resolution on the primitive-safe Add(JsonNode?) —
            // the generic Add<T> is RequiresDynamicCode and would break the IsAotCompatible contract.
            JsonNode? node = value switch {
                null => null,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                short number => JsonValue.Create(number),
                byte number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                float number => JsonValue.Create(number),
                decimal number => JsonValue.Create(number),
                char character => JsonValue.Create(character.ToString()),
                _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
            };
            array.Add(node);
        }

        return array;
    }

    private static void ApplyRange(JsonObject schema, RangeAttribute range) {
        if (TryConvertRangeOperand(range.Minimum, out var minimum))
            schema[range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum"] = minimum;

        if (TryConvertRangeOperand(range.Maximum, out var maximum))
            schema[range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum"] = maximum;
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
    private static bool IsArraySchema(JsonObject schema) {
        return schema["type"] switch {
            JsonValue single => single.TryGetValue<string>(out var type) && type == "array",
            JsonArray union => union.Any(static node =>
                node is JsonValue value && value.TryGetValue<string>(out var type) && type == "array"),
            _ => false
        };
    }

    internal static JsonNode NormalizeNumericType(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (schema is not JsonObject obj ||
            obj["type"] is not JsonArray typeArr ||
            !obj.ContainsKey("pattern"))
            return schema;

        var types = typeArr
            .Select(t => t?.GetValue<string>())
            .Where(t => t is not null)
            .ToList()!;

        var numericType = types.FirstOrDefault(t => t is "number" or "integer");
        if (numericType is null || !types.Contains("string")) return schema;

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
                Converters = { new JsonStringEnumConverter() }
            }
            : new JsonSerializerOptions(jsonOptions);

        if (options.TypeInfoResolver is null && options.TypeInfoResolverChain.Count == 0)
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();

        return options;
    }
}
