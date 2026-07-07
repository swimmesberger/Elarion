using System.Text.Json.Serialization;
using Elarion.Abstractions.Auditing;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Source-generated JSON context for the audit payload columns. Storage-only — the canonical
/// (<c>IElarionJsonSerialization</c>) options govern the wire; these columns never leave the database through
/// framework code, so a private context keeps the package free of composition-order concerns.
/// </summary>
[JsonSerializable(typeof(AuditChange[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class AuditingJsonContext : JsonSerializerContext;
