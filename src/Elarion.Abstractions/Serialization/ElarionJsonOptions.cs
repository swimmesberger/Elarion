using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The canonical, framework-wide JSON serialization configuration. Every Elarion subsystem that needs to
/// (de)serialize — JSON-RPC, MCP, idempotency, caching, outbox, settings — reads the single
/// <see cref="JsonSerializerOptions"/> materialized from this, so a host configures the JSON type context
/// once instead of threading options into each subsystem.
/// </summary>
/// <remarks>
/// This is a mutable bag of knobs plus a resolver contribution list. It is composed across layers: each
/// layer adds its source-generated <see cref="IJsonTypeInfoResolver"/> to <see cref="TypeInfoResolvers"/>
/// (transports insert their envelope contexts first, the module bootstrapper adds module contexts, the host
/// adds extras). The composed value is materialized once and frozen by <c>IElarionJsonSerialization</c>;
/// do not mutate an instance after the options have first been read.
/// <para>
/// Registered without a <c>Microsoft.Extensions.Options</c> dependency (the abstractions and core packages
/// deliberately avoid it); contributions accumulate through <c>ConfigureElarionJson</c>.
/// </para>
/// </remarks>
public sealed class ElarionJsonOptions {
    /// <summary>The property naming policy. Defaults to <see cref="JsonNamingPolicy.CamelCase"/>.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; set; } = JsonNamingPolicy.CamelCase;

    /// <summary>Whether property name matching is case-insensitive on read. Defaults to <see langword="true"/>.</summary>
    public bool PropertyNameCaseInsensitive { get; set; } = true;

    /// <summary>When to ignore a property while writing. Defaults to <see cref="JsonIgnoreCondition.WhenWritingNull"/>.</summary>
    public JsonIgnoreCondition DefaultIgnoreCondition { get; set; } = JsonIgnoreCondition.WhenWritingNull;

    /// <summary>
    /// The ordered source-generated resolvers composed into the resolver chain (first-match-wins at runtime).
    /// Transport envelope contexts insert at index 0; module and host contexts append.
    /// </summary>
    public IList<IJsonTypeInfoResolver> TypeInfoResolvers { get; } = new List<IJsonTypeInfoResolver>();

    /// <summary>
    /// Opt in to a reflection-based fallback (<see cref="DefaultJsonTypeInfoResolver"/>) appended after every
    /// source-generated resolver. Defaults to <see langword="false"/> — AOT-strict, matching the repo-wide
    /// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so a type missing from every source-gen context
    /// fails loudly instead of silently reflecting (and being trimmed away under AOT).
    /// </summary>
    public bool EnableReflectionFallback { get; set; }

    /// <summary>
    /// An advanced escape hatch applied to the materialized <see cref="JsonSerializerOptions"/> just before it is
    /// frozen — for converters, a custom encoder, or any knob not surfaced above. Runs last.
    /// </summary>
    public Action<JsonSerializerOptions>? PostConfigure { get; set; }
}
