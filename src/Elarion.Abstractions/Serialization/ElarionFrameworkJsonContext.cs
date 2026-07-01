using System.Text.Json.Serialization;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// Source-generated JSON context for the framework's own types that must always be resolvable in the canonical
/// options but are <b>not statically reachable</b> from an app's or module's <c>[JsonSerializable]</c> roots — so
/// no app context would ever register them. It is seeded into the canonical resolver chain by
/// <c>IElarionJsonSerialization</c>, so these types serialize under source generation — with no per-app
/// registration and no reflection — even on an AOT-strict host
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
/// </summary>
/// <remarks>
/// The canonical case is a payload behind a polymorphic <see cref="object"/> slot: <see cref="AppError.Data"/> is
/// typed <see cref="object"/>, so a transport serializing it (e.g. the JSON-RPC error object) dispatches on the
/// runtime type, and <see cref="System.Text.Json.JsonSerializer"/> needs a
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> for each concrete payload — which the STJ
/// source generator never pulls into a module context because the <see cref="object"/> breaks static reachability.
/// The framework's own such types live here (currently <see cref="ValidationErrorData"/>); app-provided payloads
/// (via <see cref="AppError.Validation(string, object?)"/> and friends) stay in the app's own context. When the
/// framework introduces another type in this category, add a <c>[JsonSerializable]</c> for it here — the seeding
/// logic and this type's name do not change. Kept camelCase / string-enum to match the transport envelopes.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ValidationErrorData))]
public sealed partial class ElarionFrameworkJsonContext : JsonSerializerContext;
