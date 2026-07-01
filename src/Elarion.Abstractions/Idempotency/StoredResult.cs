using System.Text.Json;

namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The AOT-safe JSON storage envelope for a replayable handler outcome. It carries the success/failure flag,
/// the stored (definitive) <see cref="AppError"/> for a failure, and — for a <c>Result&lt;T&gt;</c> success —
/// the value as <b>already-serialized</b> JSON in <see cref="Value"/>.
/// </summary>
/// <remarks>
/// The envelope is deliberately <b>non-generic</b>. The success value is not a strongly-typed member here
/// because that would make the envelope <c>StoredResult&lt;T&gt;</c>, and no source-generated
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> ever registers the closed
/// <c>StoredResult&lt;T&gt;</c> — so serializing it on an AOT-strict host
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>) throws <see cref="System.NotSupportedException"/>
/// on the first successful <c>[Idempotent]</c> command, <em>after</em> the business write committed. Instead the
/// generated payload policy serializes the value <em>separately</em> through the canonical options'
/// <c>GetTypeInfo(typeof(T))</c> (which the module-generated contexts do register, because handler response types
/// are statically reachable) and embeds the resulting JSON here as a <see cref="JsonElement"/>. Only this
/// non-generic envelope (plus <see cref="AppError"/>) needs a framework context registration, and it has one in
/// <c>ElarionFrameworkJsonContext</c>.
/// </remarks>
public sealed class StoredResult {
    /// <summary>Whether the stored outcome is a success.</summary>
    public bool Ok { get; set; }

    /// <summary>
    /// The success value as already-serialized JSON, when <see cref="Ok"/> is <see langword="true"/> and the
    /// handler returned a <c>Result&lt;T&gt;</c>. Absent for a non-generic <see cref="Result"/> success.
    /// </summary>
    public JsonElement? Value { get; set; }

    /// <summary>The error, when <see cref="Ok"/> is <see langword="false"/>.</summary>
    public AppError? Error { get; set; }
}
