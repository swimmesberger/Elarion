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
    /// reflection-free (trim/AOT-safe) and does not move when a host retunes its wire JSON. A <c>null</c> or
    /// empty column reads back as an empty array rather than throwing, so a column added by a migration without
    /// a backfill is readable.
    /// </para>
    /// <example>
    /// <code>
    /// builder.Property(e => e.Tags).HasElarionJsonStringArray();
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

    private static string SerializeStringArray(string[]? value) {
        return JsonSerializer.Serialize(
            value ?? [], ElarionEntityFrameworkCoreJsonContext.Default.StringArray);
    }

    private static string[] DeserializeStringArray(string? json) {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize(json, ElarionEntityFrameworkCoreJsonContext.Default.StringArray) ?? [];
    }
}
