using System.Text.Json.Serialization;

namespace Elarion.Session;

/// <summary>
/// The source-generated JSON serializer context for the session bootstrap's wire types. A host combines
/// <see cref="Default"/> into its <c>JsonSerializerOptions.TypeInfoResolver</c> (alongside the module resolvers) so
/// the operation serializes AOT-safely. See <c>ADR-0021</c> — a framework feature ships its own resolver.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionResponse))]
public sealed partial class SessionJsonContext : JsonSerializerContext;
