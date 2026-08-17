using System.Text.Json;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Elarion.EntityFrameworkCore;

/// <summary>Opt-in property conventions for value shapes an application would otherwise hand-roll.</summary>
public static class ElarionPropertyBuilderExtensions {
    /// <summary>
    /// Stores a <see cref="string"/> array as a JSON text column, with the matching order-dependent
    /// <see cref="ElarionValueComparers.Sequence{T}"/> attached in the same call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The converter and the comparer are a <b>pair</b>: a converted collection without a comparer is compared by
    /// reference, so EF misses in-place edits, and a comparer whose equality and hashing disagree makes change
    /// detection depend on element order by accident. Configuring both in one call is the point of this method.
    /// </para>
    /// <para>
    /// Serialization goes through this package's own source-generated context, so the column encoding is
    /// reflection-free (trim/AOT-safe) and does not move when a host retunes its wire JSON.
    /// </para>
    /// <para>
    /// <b>Null handling.</b> The converter is built with EF's default <c>convertsNulls: false</c>, so EF short
    /// circuits nulls and never calls it for them: a <c>null</c> property writes SQL <c>NULL</c>, and a
    /// <c>NULL</c> column reads back as a <c>null</c> array (not an empty one). What the converter <em>does</em>
    /// absorb is an <b>empty string</b> — the value an <c>AddColumn</c> migration with a <c>""</c> default
    /// leaves behind — which reads back as an empty array instead of throwing on invalid JSON.
    /// </para>
    /// <example>
    /// <code>
    /// builder.Property(e => e.Tags).HasElarionJsonStringArray();
    ///
    /// // A nullable string[]? property: PropertyBuilder&lt;T&gt; is invariant, so the null-forgiving operator
    /// // selects the PropertyBuilder&lt;string[]&gt; this method is declared on.
    /// builder.Property(e => e.OptionalTags!).HasElarionJsonStringArray();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="propertyBuilder">The property to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static PropertyBuilder<string[]> HasElarionJsonStringArray(
        this PropertyBuilder<string[]> propertyBuilder) {
        ArgumentNullException.ThrowIfNull(propertyBuilder);

        return propertyBuilder.HasConversion(
            value => SerializeStringArray(value),
            json => DeserializeStringArray(json),
            ElarionValueComparers.Sequence<string>());
    }

    // Both directions are only ever reached for non-null values: HasConversion builds the converter with EF's
    // default convertsNulls: false, so EF short circuits null on the way in and on the way out.
    private static string SerializeStringArray(string[] value) {
        return JsonSerializer.Serialize(value, ElarionEntityFrameworkCoreJsonContext.Default.StringArray);
    }

    private static string[] DeserializeStringArray(string json) {
        // Not a null guard (see above) — this absorbs the empty string an AddColumn migration with a ""
        // default leaves in existing rows, which is not valid JSON and would otherwise throw on first read.
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize(json, ElarionEntityFrameworkCoreJsonContext.Default.StringArray) ?? [];
    }
}
