using System.Text.Json.Serialization;

namespace Elarion.Session;

/// <summary>
/// The source-generated JSON serializer context for the session bootstrap's wire types. A host combines
/// <see cref="Default"/> into its <c>JsonSerializerOptions.TypeInfoResolver</c> (alongside the module resolvers) so
/// the operation serializes AOT-safely. See <c>ADR-0031</c> — a framework feature ships its own resolver.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionRequest))]
// Metadata-only generation is required, not a preference. The serialization fast path emits a handler that
// serializes each property through *this context's own* JsonTypeInfo instances, which are bound to this context's
// options — so a polymorphic `sections` value would be resolved against SessionJsonContext alone and throw for
// every application section type, even though the caller serialized through the canonical composed chain. The
// metadata path resolves property and polymorphic type infos from the options the response's type info is bound
// to, which is the composed chain a contributor's resolver was added to.
[JsonSerializable(typeof(SessionResponse), GenerationMode = JsonSourceGenerationMode.Metadata)]
// The contributed-sections bag. Its values are serialized from their runtime type, so each section payload type
// must reach the composed resolver chain through its own context — AddElarionClientSnapshotContributor takes one.
[JsonSerializable(typeof(IReadOnlyDictionary<string, object>), GenerationMode = JsonSourceGenerationMode.Metadata)]
public sealed partial class SessionJsonContext : JsonSerializerContext;
