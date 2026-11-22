using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Dispatch;
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
        IReadOnlyList<HandlerRoute> methods;
        try {
            methods = dispatcher.GetRegisteredRoutes();
        }
        catch (InvalidOperationException ex) {
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher is not frozen. Call Freeze() after registering all JSON-RPC methods.",
                ex);
        }

        if (methods.Count == 0)
            throw new InvalidOperationException(
                "Cannot export the JSON-RPC schema because the dispatcher has no registered methods.");

        // Constants, presets and sort keys are values, not schemas: they are written with the canonical options, plus a
        // reflection fallback for value types no source-generated context lists (an int[] constant, say).
        var valueOptions = CreateValueOptions(options);
        var methodsObj = new JsonObject();
        foreach (var route in methods) {
            var requestSchema = BuildSchemaNode(
                route.RequestType, options, SchemaDirection.Request, InjectReflectedRequestAnnotations);
            var responseSchema = BuildSchemaNode(
                route.ResponseType, options, SchemaDirection.Response, InjectReflectedResponseAnnotations);
            NarrowDecisionAnswers(requestSchema, route, options);

            var method = new JsonObject {
                ["params"] = requestSchema,
                ["result"] = responseSchema
            };
            // Only emit the flag when set, so the schema stays byte-identical for non-idempotent methods.
            if (route.Idempotent) method["idempotent"] = true;

            // The declared error contract (ADR-0080): code -> kind + optional payload schema, in ordinal code
            // order. Omitted when the operation declares none, so contract-free schemas stay byte-identical.
            if (BuildErrorsNode(route.Errors ?? [], options) is { } errorsNode) method["errors"] = errorsNode;

            // What a client may know before calling: the requirements it can evaluate against its session, whether
            // the operation only reads (an IQuery request), and its [ClientTag] hints. Each member is omitted when
            // empty, so a method without any stays byte-identical.
            if (BuildRequiresNode(route.Client) is { } requiresNode) method["requires"] = requiresNode;
            if (typeof(IQuery).IsAssignableFrom(route.RequestType)) method["readOnly"] = true;
            if (BuildTagsNode(route.Client) is { } tagsNode) method["tags"] = tagsNode;

            // Declared next to the handler (ADR-0100): named request presets and the sort keys of its sort map.
            if (exportOptions?.HandlerTypes?.TryGetValue(route.RequestType, out var handlerType) == true) {
                if (BuildPresetsNode(route, handlerType, requestSchema, valueOptions) is { } presetsNode)
                    method["presets"] = presetsNode;
                if (BuildSortKeysNode(handlerType) is { } sortKeysNode) method["sortKeys"] = sortKeysNode;
            }

            methodsObj[route.Name] = method;
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

        var constants = BuildConstantsNode(exportOptions, valueOptions);
        if (constants is not null) schema["constants"] = constants;

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonSerializerOptions CreateValueOptions(JsonSerializerOptions options) {
        var valueOptions = new JsonSerializerOptions(options);
        valueOptions.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return valueOptions;
    }

    /// <summary>
    /// Builds the <c>constants</c> block — per enabled module (camel-cased, as method names are), its
    /// <c>[ClientConstants]</c> members with their values in the canonical JSON form — or <see langword="null"/> when no
    /// module exports any, keeping constant-free schemas byte-identical. Modules and names are in ordinal order.
    /// </summary>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonObject? BuildConstantsNode(
        JsonRpcSchemaExportOptions? exportOptions, JsonSerializerOptions valueOptions) {
        if (exportOptions?.ClientConstants is not { Count: > 0 } sets) return null;

        var constants = new JsonObject();
        foreach (var group in sets
                     .GroupBy(static set => CamelCase(set.Module), StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal)) {
            var module = new JsonObject();
            foreach (var constant in group
                         .SelectMany(static set => set.Constants)
                         .DistinctBy(static constant => constant.Name, StringComparer.Ordinal)
                         .OrderBy(static constant => constant.Name, StringComparer.Ordinal)) {
                object? value;
                try {
                    value = constant.GetValue();
                }
                catch (Exception ex) {
                    throw new InvalidOperationException(
                        $"Cannot export client constant '{constant.Name}' of module '{group.Key}': reading it threw: {ex.Message}",
                        ex);
                }

                module[constant.Name] = JsonSerializer.SerializeToNode(value, constant.Type, valueOptions);
            }

            if (module.Count > 0) constants[group.Key] = module;
        }

        return constants.Count > 0 ? constants : null;
    }

    /// <summary>
    /// Builds a method's <c>presets</c> object — each <see cref="RequestPresetAttribute"/> field of its handler, in
    /// ordinal name order — or <see langword="null"/> when the handler declares none. A preset carries the members a
    /// client must send to make the server see exactly the field's value: every required member, and every other member
    /// whose value differs from what the server assumes when it is left out (an explicit <c>null</c> where that
    /// default is not null). Values are written with the canonical options, so names and enum spellings match calls.
    /// </summary>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonObject? BuildPresetsNode(
        HandlerRoute route, Type handlerType, JsonNode requestSchema, JsonSerializerOptions valueOptions) {
        var fields = handlerType
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(static field => (Field: field, Preset: field.GetCustomAttribute<RequestPresetAttribute>()))
            .Where(static pair => pair.Preset is not null)
            .OrderBy(static pair => pair.Preset!.Name, StringComparer.Ordinal)
            .ToList();
        if (fields.Count == 0) return null;

        var required = (requestSchema as JsonObject)?["required"] is JsonArray names
            ? names.Select(static name => name?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var typeInfo = valueOptions.GetTypeInfo(route.RequestType);

        var presets = new JsonObject();
        foreach (var (field, preset) in fields) {
            var value = field.GetValue(null);
            if (value is null || !route.RequestType.IsInstanceOfType(value))
                throw new InvalidOperationException(
                    $"Cannot export request preset '{preset!.Name}' of method '{route.Name}': field '{handlerType}.{field.Name}' "
                    + $"must hold a '{route.RequestType}' value.");

            presets[preset!.Name] = PresetNode(route, preset.Name, value, typeInfo, required);
        }

        return presets;
    }

    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static JsonObject PresetNode(
        HandlerRoute route, string name, object value, JsonTypeInfo typeInfo, HashSet<string> required) {
        if (JsonSerializer.SerializeToNode(value, typeInfo) is not JsonObject written)
            throw new InvalidOperationException(
                $"Cannot export request preset '{name}' of method '{route.Name}': its request does not serialize as an object.");

        // What the server reads when only the required members are sent: deserialize them alone, so constructor
        // parameter defaults and initializers apply exactly as they do for a call, and write that instance back.
        var defaults = new JsonObject();
        foreach (var member in required)
            if (written.TryGetPropertyValue(member, out var memberValue))
                defaults[member] = memberValue?.DeepClone();
            else
                throw new InvalidOperationException(
                    $"Cannot export request preset '{name}' of method '{route.Name}': it leaves the required member "
                    + $"'{member}' null. Give it a value; a preset is a complete request.");

        JsonObject? assumed;
        try {
            assumed = JsonSerializer.SerializeToNode(defaults.Deserialize(typeInfo), typeInfo) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) {
            // A request that cannot be built from its required members alone: every written member is the preset.
            assumed = null;
        }

        var node = new JsonObject();
        foreach (var (member, memberValue) in written) {
            if (required.Contains(member)
                || assumed is null
                || !assumed.TryGetPropertyValue(member, out var assumedValue)
                || !JsonNode.DeepEquals(memberValue, assumedValue))
                node[member] = memberValue?.DeepClone();
        }

        // A member the preset leaves null (so it was not written) whose default is not null: the client must send null.
        if (assumed is not null)
            foreach (var (member, assumedValue) in assumed)
                if (assumedValue is not null && !written.ContainsKey(member))
                    node[member] = null;

        return node;
    }

    private const string SortMapTypeName = "Elarion.Paging.SortMap`1";

    /// <summary>
    /// Builds a method's <c>sortKeys</c> array from the one <c>SortMap</c> static field its handler declares — each key
    /// in declaration order with the direction a bare key applies and, on the default key, <c>default: true</c> — or
    /// <see langword="null"/> when the handler declares none or more than one (the generator warns, ELRPC012).
    /// </summary>
    /// <remarks>
    /// The map is read by reflection because this build-time export references no paging package; the shape read
    /// (<c>Keys</c> with <c>Key</c>, <c>Direction</c>, <c>IsDefault</c>) is <c>SortMap&lt;T&gt;.Keys</c>.
    /// </remarks>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    private static JsonArray? BuildSortKeysNode(Type handlerType) {
        var maps = handlerType
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(static field => field.FieldType is { IsGenericType: true } type
                                   && type.GetGenericTypeDefinition().FullName == SortMapTypeName)
            .ToList();
        if (maps is not [var map] || map.GetValue(null) is not { } sortMap) return null;

        if (sortMap.GetType().GetProperty("Keys")?.GetValue(sortMap) is not System.Collections.IEnumerable keys)
            return null;

        var array = new JsonArray();
        foreach (var key in keys) {
            var type = key.GetType();
            var entry = new JsonObject {
                ["key"] = (string?)type.GetProperty("Key")?.GetValue(key),
                ["direction"] = type.GetProperty("Direction")?.GetValue(key)?.ToString() == "Descending"
                    ? "descending"
                    : "ascending"
            };
            if (type.GetProperty("IsDefault")?.GetValue(key) is true) entry["default"] = true;
            array.Add((JsonNode)entry);
        }

        return array.Count > 0 ? array : null;
    }

    // A module name as a method-name prefix ("Billing" -> "billing"); RpcMethodEmission.CamelCaseModule's counterpart.
    private static string CamelCase(string name) {
        return name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
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
                entry["data"] = BuildSchemaNode(dataType, options, SchemaDirection.Response, InjectReflectedResponseAnnotations);

            // A declared decision (ADR-0101): the options the caller picks from, and whether it answers per item.
            if (error.Decision is { } decision) {
                // Declaration order, not sorted: the options read as the dialog offers them.
                var decisionNode = new JsonObject {
                    ["options"] = new JsonArray(decision.Options.Select(static o => (JsonNode?)JsonValue.Create(o)).ToArray())
                };
                if (decision.PerItem) decisionNode["perItem"] = true;
                entry["decision"] = decisionNode;
            }

            node[error.Code] = entry;
        }

        return node;
    }

    /// <summary>
    /// Narrows the items of the request's <see cref="IDecidingRequest.Decisions"/> member to the decisions the method
    /// declares (ADR-0101), so the schema states which answers the request takes: per declared code, in ordinal order,
    /// an answer whose <c>code</c> is that code, whose <c>option</c> is one of its options (optional for a
    /// confirmation, where it defaults to <c>confirm</c>), and which names its <c>item</c> for a per-item decision. One
    /// decision is that answer object, several are an <c>anyOf</c> of them. A method that declares no decision, or a
    /// request without the member, keeps the member's schema as the serializer exports it.
    /// </summary>
    /// <remarks>
    /// The schema describes the answers that count; the server still reads an undeclared one as no answer and asks
    /// again, so no pre-handler check depends on it.
    /// </remarks>
    [RequiresUnreferencedCode(SchemaReflectionMessage)]
    [RequiresDynamicCode(SchemaReflectionMessage)]
    private static void NarrowDecisionAnswers(JsonNode requestSchema, HandlerRoute route, JsonSerializerOptions options) {
        if (route.Errors is not { Count: > 0 } errors || !typeof(IDecidingRequest).IsAssignableFrom(route.RequestType))
            return;

        var decisions = errors
            .Where(static error => error.Decision is not null)
            .OrderBy(static error => error.Code, StringComparer.Ordinal)
            .ToList();
        if (decisions.Count == 0) return;

        // Wire names come from the serializer's contract, so a naming policy or [JsonPropertyName] is honored.
        if (WireName(options.GetTypeInfo(route.RequestType), nameof(IDecidingRequest.Decisions)) is not { } member
            || requestSchema is not JsonObject request
            || request["properties"]?[member] is not JsonObject property)
            return;

        var answer = options.GetTypeInfo(typeof(DecisionAnswer));
        var code = WireName(answer, nameof(DecisionAnswer.Code)) ?? "code";
        var item = WireName(answer, nameof(DecisionAnswer.Item)) ?? "item";
        var option = WireName(answer, nameof(DecisionAnswer.Option)) ?? "option";

        var cases = new List<JsonNode>(decisions.Count);
        foreach (var error in decisions) {
            var decision = error.Decision!;
            var properties = new JsonObject { [code] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(error.Code) } };
            var required = new List<string> { code };
            if (decision.PerItem) {
                properties[item] = new JsonObject { ["type"] = "string" };
                required.Add(item);
            }

            // Declaration order, as the errors node lists the options.
            properties[option] = new JsonObject {
                ["type"] = "string",
                ["enum"] = new JsonArray(decision.Options.Select(static o => (JsonNode?)JsonValue.Create(o)).ToArray())
            };
            if (decision.Options is not [DecisionAnswer.Confirm]) required.Add(option);

            cases.Add(new JsonObject {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(static name => (JsonNode?)JsonValue.Create(name)).ToArray())
            });
        }

        property["items"] = cases.Count == 1 ? cases[0] : new JsonObject { ["anyOf"] = new JsonArray([.. cases]) };
    }

    // The JSON name of the member declared as clrName, or null when the contract does not serialize it.
    private static string? WireName(JsonTypeInfo typeInfo, string clrName) {
        return typeInfo.Properties.FirstOrDefault(p => p.AttributeProvider is MemberInfo member && member.Name == clrName)?.Name;
    }

    /// <summary>
    /// Builds a method's <c>requires</c> object — <c>anonymous</c>, or <c>authenticated</c> with the
    /// <c>permissions</c> and <c>roles</c> a client can check and <c>serverChecks</c> for what only the server can —
    /// or <see langword="null"/> when the operation declares nothing a client could act on.
    /// </summary>
    private static JsonObject? BuildRequiresNode(HandlerClientMetadata? client) {
        if (client is null) return null;
        if (client.AllowAnonymous) return new JsonObject { ["anonymous"] = true };

        var node = new JsonObject();
        if (client.RequiresAuthentication) node["authenticated"] = true;
        if (client.Permissions.Count > 0) node["permissions"] = StringArray(client.Permissions);
        if (client.Roles.Count > 0) node["roles"] = StringArray(client.Roles);
        if (client.HasServerChecks) node["serverChecks"] = true;
        return node.Count > 0 ? node : null;
    }

    /// <summary>Builds a method's <c>tags</c> object in ordinal key order, or <see langword="null"/> when it has none.</summary>
    private static JsonObject? BuildTagsNode(HandlerClientMetadata? client) {
        if (client is not { Tags.Count: > 0 }) return null;

        var node = new JsonObject();
        foreach (var (key, value) in client.Tags.OrderBy(static tag => tag.Key, StringComparer.Ordinal))
            node[key] = value;

        return node;
    }

    private static JsonArray StringArray(IEnumerable<string> values) {
        return new JsonArray(values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
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
                    topic.EventType, options, SchemaDirection.Response, InjectReflectedResponseAnnotations)
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
                var node = ApplyRequiredness(
                    ctx, NormalizeNumericType(ctx, MapUploadPayload(ctx, MapFilePayload(ctx, schema), direction)), direction);
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
        // A collection of files: the exporter leaves out the "items" of a converter-backed element (see
        // MapUploadPayload), so the list would export as an array of anything; put the envelope back.
        if (ctx.TypeInfo is { Kind: JsonTypeInfoKind.Enumerable } collection
            && collection.ElementType == typeof(Abstractions.ElarionFile)
            && schema is JsonObject array
            && !array.ContainsKey("items")) {
            array["items"] = FileNode();
            return schema;
        }

        return ctx.TypeInfo.Type == typeof(Abstractions.ElarionFile) ? FileNode() : schema;
    }

    private static JsonObject FileNode() {
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
    /// Replaces the schema node for <see cref="Elarion.Abstractions.UploadedFile"/>, opaque to the exporter because the
    /// type serializes through its converter, with the converter's wire form: a string, the blob id of a staged upload
    /// (ADR-0105). The node is marked <c>x-elarion-upload: true</c> so the TypeScript client generator accepts a
    /// native <c>File</c> there and uploads it before the call. The replacement drops the exporter's own nullability,
    /// so a nullable member gets <c>null</c> back from the property's annotation.
    /// </summary>
    private static JsonNode MapUploadPayload(JsonSchemaExporterContext ctx, JsonNode schema, SchemaDirection direction) {
        if (ctx.TypeInfo.Type == typeof(Abstractions.UploadedFile))
            return UploadNode(
                ctx.PropertyInfo is { } property
                && (direction == SchemaDirection.Request ? property.IsSetNullable : property.IsGetNullable),
                FindUploadLimits(ctx.PropertyInfo));

        // The exporter leaves out the "items" of a collection whose element schema accepts anything, which is what a
        // converter-backed element exports as, before this transform ever sees the element: put it back. The member's
        // declared limits apply to every file of the collection.
        if (ctx.TypeInfo is { Kind: JsonTypeInfoKind.Enumerable } collection
            && collection.ElementType == typeof(Abstractions.UploadedFile)
            && schema is JsonObject array
            && !array.ContainsKey("items"))
            array["items"] = UploadNode(false, FindUploadLimits(ctx.PropertyInfo));

        return schema;
    }

    /// <summary>The vendor keyword carrying an upload member's declared largest size in bytes (<see cref="UploadLimitsAttribute"/>).</summary>
    internal const string MaxBytesKeyword = "x-elarion-max-bytes";

    /// <summary>The vendor keyword carrying an upload member's declared accepted media types (<see cref="UploadLimitsAttribute"/>).</summary>
    internal const string ContentTypesKeyword = "x-elarion-content-types";

    private static JsonObject UploadNode(bool nullable, UploadLimitsAttribute? limits) {
        var node = new JsonObject {
            ["type"] = nullable ? new JsonArray("string", "null") : "string",
            ["x-elarion-upload"] = true,
            ["description"] = "A file staged through the upload endpoint; the value is the blob id it answered with."
        };

        // Declared limits (ADR-0100) travel next to the marker, so a client that predates them still sees an upload.
        if (limits?.MaxBytes > 0) node[MaxBytesKeyword] = limits.MaxBytes;
        if (limits?.ContentTypes is { Length: > 0 } contentTypes)
            node[ContentTypesKeyword] = new JsonArray(contentTypes.Select(static t => (JsonNode?)JsonValue.Create(t)).ToArray());

        return node;
    }

    // [UploadLimits] on the member, or on the positional record's constructor parameter.
    private static UploadLimitsAttribute? FindUploadLimits(JsonPropertyInfo? property) {
        return FindUploadLimits(property?.AttributeProvider) ?? FindUploadLimits(property?.AssociatedParameter?.AttributeProvider);
    }

    private static UploadLimitsAttribute? FindUploadLimits(ICustomAttributeProvider? provider) {
        return provider?.GetCustomAttributes(typeof(UploadLimitsAttribute), true) is [UploadLimitsAttribute found, ..]
            ? found
            : null;
    }

    /// <summary>
    /// Composes the reflective per-property transforms applied to the full schema export: the
    /// <see cref="DescriptionAttribute"/> description plus the DataAnnotations constraint keywords, per direction.
    /// </summary>
    private static JsonNode InjectReflectedRequestAnnotations(JsonSchemaExporterContext ctx, JsonNode schema) {
        return InjectReflectedConstraints(ctx, InjectReflectedDescription(ctx, schema), SchemaDirection.Request);
    }

    /// <inheritdoc cref="InjectReflectedRequestAnnotations"/>
    private static JsonNode InjectReflectedResponseAnnotations(JsonSchemaExporterContext ctx, JsonNode schema) {
        return InjectVisibleWith(ctx,
            InjectReflectedConstraints(ctx, InjectReflectedDescription(ctx, schema), SchemaDirection.Response));
    }

    /// <summary>
    /// The vendor keyword naming the permission a caller must hold to receive a response member
    /// (<see cref="VisibleWithAttribute"/>, ADR-0103) — a string, or an array when any one of several permissions shows
    /// it (<see cref="VisibleWithAttribute.AnyOf"/>); the pipeline omits the member for everyone else.
    /// </summary>
    internal const string VisibleWithKeyword = "x-elarion-visible-with";

    /// <summary>
    /// Adds <see cref="VisibleWithKeyword"/> to a response member marked <see cref="VisibleWithAttribute"/> — on the
    /// property, or on the positional record's constructor parameter — so a generated client can say why the member
    /// may be missing.
    /// </summary>
    private static JsonNode InjectVisibleWith(JsonSchemaExporterContext ctx, JsonNode schema) {
        if (ctx.PropertyInfo is not { } property || schema is not JsonObject obj) return schema;

        var attribute = FindVisibleWith(property.AttributeProvider)
                        ?? FindVisibleWith(property.AssociatedParameter?.AttributeProvider);
        if (attribute?.Permissions is { Count: > 0 } permissions)
            obj[VisibleWithKeyword] = permissions.Count == 1
                ? JsonValue.Create(permissions[0])
                : new JsonArray([.. permissions.Distinct(StringComparer.Ordinal).Select(static p => (JsonNode?)p)]);

        return schema;
    }

    private static VisibleWithAttribute? FindVisibleWith(ICustomAttributeProvider? provider) {
        return provider?.GetCustomAttributes(typeof(VisibleWithAttribute), true) is [VisibleWithAttribute found, ..]
            ? found
            : null;
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
    /// The vendor keyword carrying a constraint's explicit error message, keyed by the constraint keyword it
    /// belongs to (e.g. <c>{"pattern": "Code must be three capital letters."}</c>). Read by the TypeScript client
    /// generator so client-side pre-validation reports the same message as the server.
    /// </summary>
    internal const string ErrorMessagesKeyword = "x-elarion-error-messages";

    /// <summary>
    /// Injects JSON Schema validation keywords from a property's
    /// <c>System.ComponentModel.DataAnnotations</c> attributes, mirroring
    /// <c>Microsoft.AspNetCore.OpenApi</c>'s attribute→keyword mapping (plus
    /// <see cref="EmailAddressAttribute"/> → <c>format: "email"</c> and
    /// <see cref="AllowedValuesAttribute"/> → <c>enum</c>) so the JSON-RPC schema, the MCP tool input
    /// schemas, and the OpenAPI document agree on the same declared constraints (ADR-0027). Attributes apply in
    /// declaration order with last-wins on duplicate keywords, matching Microsoft. Reads the attributes
    /// reflectively — appropriate for this build-time exporter.
    /// <para>
    /// <c>pattern</c> is exported anchored (<c>^(?:…)$</c>): the server's <see cref="RegularExpressionAttribute"/>
    /// requires the whole value to match, while JSON Schema (and every client validator built on it) searches, so
    /// an unanchored export would admit values the server rejects. A <see cref="RequiredAttribute"/> on a string
    /// (with <see cref="RequiredAttribute.AllowEmptyStrings"/> off) rejects empty and whitespace-only values on the
    /// server; it is exported as a not-blank lookahead on the same anchored pattern, combined with a regular
    /// expression when the member has both (the regular expression's explicit message wins the shared keyword).
    /// Requiredness itself still comes from nullability (ADR-0082).
    /// </para>
    /// <para>
    /// Both tightenings apply to <see cref="SchemaDirection.Request"/> schemas only — the server validates requests, and
    /// the schema must reject exactly what it rejects. Results and event payloads are never validated on the server, yet
    /// the generated client parses them: a stored value that predates a constraint (a blank name, a code from before
    /// the pattern) must not fail a read. They keep the pattern as declared and carry no not-blank lookahead.
    /// </para>
    /// </summary>
    /// <remarks>
    /// An attribute with an explicit message (<see cref="ValidationAttribute.ErrorMessage"/> or
    /// <see cref="ValidationAttribute.ErrorMessageResourceName"/>) also records that message under
    /// <see cref="ErrorMessagesKeyword"/> for every keyword it produced, formatted exactly as the server's
    /// validator formats it (<see cref="ValidationAttribute.FormatErrorMessage"/> with the member's display
    /// name), so a client shows the same text before and after the round trip. Resource-based messages are
    /// resolved once, in the exporting process's UI culture. Default framework messages stay out of the schema:
    /// they are generic English text a client library already has its own wording for.
    /// </remarks>
    internal static JsonNode InjectReflectedConstraints(JsonSchemaExporterContext ctx, JsonNode schema) {
        return InjectReflectedConstraints(ctx, schema, SchemaDirection.Request);
    }

    /// <inheritdoc cref="InjectReflectedConstraints(JsonSchemaExporterContext, JsonNode)"/>
    internal static JsonNode InjectReflectedConstraints(
        JsonSchemaExporterContext ctx, JsonNode schema, SchemaDirection direction) {
        if (ctx.PropertyInfo?.AttributeProvider is not { } provider || schema is not JsonObject obj) return schema;

        JsonObject? messages = null;
        // The pattern keyword is assembled after the loop: a regular expression and a not-blank [Required] share it,
        // whichever order they are declared in.
        string? regularExpression = null;
        string? regularExpressionMessage = null;
        var notBlank = false;
        string? notBlankMessage = null;
        // Type patterns intentionally match subclasses: a reusable custom constraint deriving a mapped
        // attribute (e.g. a [Slug] : RegularExpressionAttribute) reaches every schema surface for free.
        foreach (var attribute in provider.GetCustomAttributes(false)) {
            switch (attribute) {
                case RegularExpressionAttribute pattern:
                    regularExpression = pattern.Pattern;
                    regularExpressionMessage = ExplicitErrorMessage(pattern, provider);
                    continue;
                case RequiredAttribute { AllowEmptyStrings: false } required
                    when direction == SchemaDirection.Request && IsStringSchema(obj):
                    notBlank = true;
                    notBlankMessage = ExplicitErrorMessage(required, provider);
                    continue;
            }

            var keywords = ApplyConstraint(obj, attribute);
            if (keywords.Length == 0) continue;

            // Last-wins per keyword, like the keyword itself: a later attribute without an explicit message
            // must not leave an earlier attribute's message attached to its own bound.
            var message = ExplicitErrorMessage((ValidationAttribute)attribute, provider);
            foreach (var keyword in keywords)
                if (message is null) messages?.Remove(keyword);
                else (messages ??= new JsonObject())[keyword] = message;
        }

        if (regularExpression is not null || notBlank) {
            obj["pattern"] = direction == SchemaDirection.Request
                ? AnchoredPattern(regularExpression, notBlank)
                : regularExpression;
            if ((regularExpressionMessage ?? notBlankMessage) is { } patternMessage)
                (messages ??= new JsonObject())["pattern"] = patternMessage;
        }

        if (messages is { Count: > 0 }) obj[ErrorMessagesKeyword] = messages;

        return schema;
    }

    /// <summary>
    /// The exported <c>pattern</c>: <paramref name="regularExpression"/> (or any text) anchored to the whole value,
    /// preceded by a not-blank lookahead when <paramref name="notBlank"/>. Both lookahead and non-capturing groups
    /// mean the same in .NET and ECMAScript regular expressions. A pattern that already anchors both ends (and has
    /// no top-level alternation the anchors would not cover) is exported unchanged.
    /// </summary>
    internal static string AnchoredPattern(string? regularExpression, bool notBlank) {
        if (notBlank) return $@"^(?=[\s\S]*\S)(?:{regularExpression ?? @"[\s\S]*"})$";

        return IsWhollyAnchored(regularExpression!) ? regularExpression! : $"^(?:{regularExpression})$";
    }

    // "^…$" with no '|' outside a group or character class: the anchors then bind every alternative, so the pattern
    // already means a full match. Escapes are skipped so "\$" or "\|" do not count; anything unusual wraps.
    private static bool IsWhollyAnchored(string pattern) {
        if (pattern.Length < 2 || pattern[0] != '^' || pattern[^1] != '$') return false;

        var depth = 0;
        var inClass = false;
        for (var i = 1; i < pattern.Length - 1; i++)
            switch (pattern[i]) {
                case '\\':
                    // The closing '$' must not be the escaped character.
                    if (i + 1 == pattern.Length - 1) return false;

                    i++;
                    break;
                case '[' when !inClass:
                    inClass = true;
                    break;
                case ']' when inClass:
                    inClass = false;
                    break;
                case '(' when !inClass:
                    depth++;
                    break;
                case ')' when !inClass:
                    depth--;
                    break;
                case '|' when !inClass && depth == 0:
                    return false;
            }

        return depth == 0 && !inClass;
    }

    private static bool IsStringSchema(JsonObject schema) {
        return schema["type"] switch {
            JsonValue single => single.TryGetValue<string>(out var type) && type == "string",
            JsonArray union => union.Any(static node =>
                node is JsonValue value && value.TryGetValue<string>(out var type) && type == "string"),
            _ => false
        };
    }

    /// <summary>
    /// Writes the JSON Schema keywords one DataAnnotations attribute maps to and returns the keywords written
    /// (empty for an attribute without a mapping, or a <see cref="RangeAttribute"/> bound that does not fit).
    /// </summary>
    private static string[] ApplyConstraint(JsonObject obj, object attribute) {
        switch (attribute) {
            case RangeAttribute range:
                return ApplyRange(obj, range);
            case MinLengthAttribute minLength: {
                var keyword = IsArraySchema(obj) ? "minItems" : "minLength";
                obj[keyword] = minLength.Length;
                return [keyword];
            }
            case MaxLengthAttribute maxLength: {
                var keyword = IsArraySchema(obj) ? "maxItems" : "maxLength";
                obj[keyword] = maxLength.Length;
                return [keyword];
            }
            case LengthAttribute length: {
                var isArray = IsArraySchema(obj);
                obj[isArray ? "minItems" : "minLength"] = length.MinimumLength;
                obj[isArray ? "maxItems" : "maxLength"] = length.MaximumLength;
                return isArray ? ["minItems", "maxItems"] : ["minLength", "maxLength"];
            }
            case StringLengthAttribute stringLength:
                if (stringLength.MinimumLength > 0) obj["minLength"] = stringLength.MinimumLength;

                obj["maxLength"] = stringLength.MaximumLength;
                return stringLength.MinimumLength > 0 ? ["minLength", "maxLength"] : ["maxLength"];
            case UrlAttribute:
                obj["format"] = "uri";
                return ["format"];
            case EmailAddressAttribute:
                obj["format"] = "email";
                return ["format"];
            case Base64StringAttribute:
                obj["format"] = "byte";
                return ["format"];
            case AllowedValuesAttribute allowedValues:
                obj["enum"] = CreateEnumArray(allowedValues.Values);
                return ["enum"];
            default:
                return [];
        }
    }

    /// <summary>
    /// The message the server reports for a violation of <paramref name="attribute"/> when the attribute declares
    /// one explicitly, or <see langword="null"/> when it relies on the framework default. Formats with the same
    /// display name the generated validation metadata uses: <c>[Display(Name = …)]</c>, else the CLR member name.
    /// </summary>
    private static string? ExplicitErrorMessage(ValidationAttribute attribute, ICustomAttributeProvider provider) {
        if (attribute.ErrorMessageResourceName is null &&
            (attribute.ErrorMessage is not { } message || message == FrameworkDefaultMessage(attribute)))
            return null;

        var displayName = provider.GetCustomAttributes(typeof(DisplayAttribute), false)
            is [DisplayAttribute { Name.Length: > 0 } display, ..]
            ? display.Name
            : (provider as MemberInfo)?.Name ?? string.Empty;

        try {
            return attribute.FormatErrorMessage(displayName);
        }
        catch (InvalidOperationException ex) {
            throw new InvalidOperationException(
                $"Cannot export the error message of [{attribute.GetType().Name}] on '{displayName}': {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// The built-in message a <see cref="DataTypeAttribute"/>-derived framework attribute reports through
    /// <see cref="ValidationAttribute.ErrorMessage"/> without being given one (the other mapped attributes keep
    /// their default behind a resource accessor, so their <c>ErrorMessage</c> is <see langword="null"/>). Built
    /// per call so it follows the current UI culture like the attribute instance did.
    /// </summary>
    private static string? FrameworkDefaultMessage(ValidationAttribute attribute) {
        return attribute switch {
            EmailAddressAttribute => new EmailAddressAttribute().ErrorMessage,
            UrlAttribute => new UrlAttribute().ErrorMessage,
            Base64StringAttribute => new Base64StringAttribute().ErrorMessage,
            _ => null
        };
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

    private static string[] ApplyRange(JsonObject schema, RangeAttribute range) {
        string? minimumKeyword = null;
        string? maximumKeyword = null;
        if (TryConvertRangeOperand(range.Minimum, out var minimum)) {
            minimumKeyword = range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum";
            schema[minimumKeyword] = minimum;
        }

        if (TryConvertRangeOperand(range.Maximum, out var maximum)) {
            maximumKeyword = range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum";
            schema[maximumKeyword] = maximum;
        }

        return (minimumKeyword, maximumKeyword) switch {
            ({ } min, { } max) => [min, max],
            ({ } min, null) => [min],
            (null, { } max) => [max],
            _ => []
        };
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
