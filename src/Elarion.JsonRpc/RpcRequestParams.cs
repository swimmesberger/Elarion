using System.Text.Json;

namespace Elarion.JsonRpc;

/// <summary>
/// Constructs a route's request object from a transport's optional <c>params</c>/arguments element, shared by the
/// JSON-RPC dispatcher and the MCP tool invoker so both treat "omitted params" identically.
/// </summary>
/// <remarks>
/// An omitted (or <see cref="JsonValueKind.Undefined"/>) params element is deserialized as an empty JSON object
/// (<c>{}</c>) through the same source-generated <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>
/// used for a present element — never via <see cref="Activator.CreateInstance(Type)"/>. This keeps construction
/// reflection-free and Native-AOT / trimming safe, and applies the request type's constructor defaults correctly:
/// an all-optional positional record (which has no public parameterless constructor) "just works", and omitted
/// <c>params</c> produces the same object as an explicit <c>params: {}</c>.
/// </remarks>
internal static class RpcRequestParams {
    // A detached JsonElement for "{}" — cloned from its parsed document so it does not depend on a disposed
    // JsonDocument. Reading (deserializing) from a JsonElement is thread-safe, so this shared instance is reused
    // across every omitted-params dispatch.
    private static readonly JsonElement EmptyObject = ParseEmptyObject();

    private static JsonElement ParseEmptyObject() {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Deserializes the request object from <paramref name="paramsElement"/>, treating an omitted/undefined element
    /// as an empty object so all-optional request records apply their defaults. Throws <see cref="JsonException"/>
    /// on malformed params (the caller maps that to an invalid-params error); returns <see langword="null"/> only
    /// when the element itself is JSON <c>null</c>.
    /// </summary>
    internal static object? Deserialize(JsonElement? paramsElement, Type requestType, JsonSerializerOptions options) {
        var element = paramsElement is { ValueKind: not JsonValueKind.Undefined } present ? present : EmptyObject;
        return element.Deserialize(options.GetTypeInfo(requestType));
    }
}
