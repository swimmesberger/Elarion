using System.Text.Json.Serialization.Metadata;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The runtime half of the one requiredness rule (ADR-0082): a nullable member is optional on the wire. System.Text.Json
/// with <c>RespectRequiredConstructorParameters</c> treats every constructor parameter without a default as required,
/// nullable or not, and honors the C# <c>required</c> modifier and <c>[JsonRequired]</c> the same way — while the
/// exported schema derives requiredness from nullability alone. This modifier makes the serializer agree with the
/// schema: an omitted nullable member binds <see langword="null"/> instead of failing with a missing-property error.
/// Non-nullable members keep the serializer's behavior (a constructor parameter default, or an initializer, is the
/// only way to make them optional), which is exactly what the schema exports.
/// </summary>
/// <remarks>
/// It is applied as a type-info modifier wrapped around every resolver of the canonical chain, so it works for
/// source-generated contexts too and needs no reflection.
/// </remarks>
internal static class WireRequiredness {
    public static void Apply(JsonTypeInfo typeInfo) {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

        foreach (var property in typeInfo.Properties)
            if (property.IsRequired && property.IsSetNullable)
                property.IsRequired = false;
    }
}
