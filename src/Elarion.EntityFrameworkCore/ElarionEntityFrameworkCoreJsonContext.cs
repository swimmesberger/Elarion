using System.Text.Json.Serialization;

namespace Elarion.EntityFrameworkCore;

/// <summary>
/// The source-generated metadata for the value shapes this package converts to JSON columns.
/// </summary>
/// <remarks>
/// Package-internal and deliberately tiny: the converters are compiled into the model, so they must not depend
/// on the reflection-based serializer (the repository builds with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, and a trimmed or NativeAOT host would lose it
/// anyway). It is also independent of the host's canonical <c>IElarionJsonSerialization</c> — a value converter
/// runs inside EF's model with no service provider in reach, and a column's storage encoding must not drift when
/// a host retunes its wire JSON.
/// </remarks>
[JsonSerializable(typeof(string[]))]
internal sealed partial class ElarionEntityFrameworkCoreJsonContext : JsonSerializerContext;
