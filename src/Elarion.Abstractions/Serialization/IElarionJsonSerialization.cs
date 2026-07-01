using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The accessor for Elarion's canonical <see cref="JsonSerializerOptions"/>. Subsystems depend on this instead
/// of a bare <see cref="JsonSerializerOptions"/> in DI, so the framework never collides with a host's own
/// <see cref="JsonSerializerOptions"/> registration (e.g. ASP.NET Core / MVC).
/// </summary>
/// <remarks>
/// The implementation materializes the options once from the composed <see cref="ElarionJsonOptions"/> and
/// freezes them (<see cref="JsonSerializerOptions.MakeReadOnly()"/>) on first access, so all consumers share one
/// stable, thread-safe instance.
/// </remarks>
public interface IElarionJsonSerialization {
    /// <summary>The materialized, frozen canonical options.</summary>
    JsonSerializerOptions Options { get; }

    /// <summary>
    /// The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/> from the canonical resolver chain. Throws
    /// when <typeparamref name="T"/> is not in any source-generated context and the reflection fallback is off.
    /// </summary>
    JsonTypeInfo<T> GetTypeInfo<T>();

    /// <summary>The <see cref="JsonTypeInfo"/> for <paramref name="type"/> from the canonical resolver chain.</summary>
    JsonTypeInfo GetTypeInfo(Type type);
}
