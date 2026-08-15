using Microsoft.EntityFrameworkCore;

namespace Elarion.EntityFrameworkCore;

/// <summary>Opt-in model-wide conventions for value shapes an application would otherwise hand-roll.</summary>
public static class ElarionModelBuilderExtensions {
    /// <summary>
    /// Stores every enum property in the model as its <b>name</b> rather than its ordinal, unless the property
    /// already declares a conversion or a provider type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A text column survives reordering or inserting enum members, and reads as itself in a database a human
    /// migrates and debugs; an ordinal column silently re-points every existing row when the enum is edited. The
    /// cost is a wider column and a text comparison — at Elarion's target tier (ADR-0025) that is not the
    /// bottleneck, which is why this is worth a one-line opt-in rather than a per-property
    /// <c>HasConversion&lt;string&gt;()</c> repeated across a configuration file.
    /// </para>
    /// <para>
    /// It is a <b>post-pass</b>: call it at the end of <c>OnModelCreating</c>, after the entity configurations
    /// have run, so it sees the finished property set (navigation-discovered entities included). Nullable enums
    /// are covered. Any property that already has a value converter, a converter type, or an explicit provider
    /// CLR type is left alone — deliberate configuration always wins, so one enum stored as an ordinal opts out
    /// by saying so in its configuration.
    /// </para>
    /// <para>
    /// There is deliberately no options bag: the convention is the whole decision, and per-property deviation is
    /// already expressible in the property's own configuration.
    /// </para>
    /// <example>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder) {
    ///     base.OnModelCreating(modelBuilder);
    ///     ConfigureEntities(modelBuilder);              // generated
    ///     modelBuilder.UseElarionEnumStringConversions();
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="modelBuilder">The model to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static ModelBuilder UseElarionEnumStringConversions(this ModelBuilder modelBuilder) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        foreach (var property in entityType.GetProperties().ToList()) {
            // Any conversion already in the model — explicit, data-annotated, or contributed by another
            // convention — is a deliberate storage choice for that property; never overwrite it. The
            // converter-type annotation is the HasConversion<TConverter>() form, which is not resolved into a
            // converter instance until the model is finalized, so it is read directly (as the generated
            // client-assigned-keys pass reads its store-default annotations).
            if (property.GetValueConverter() is not null ||
                property.FindAnnotation("ValueConverterType") is not null ||
                property.GetProviderClrType() is not null)
                continue;

            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!clrType.IsEnum)
                continue;

            // Setting the provider type (rather than a converter instance) is exactly what
            // HasConversion<string>() does: EF resolves the enum-to-string converter, including the nullable
            // wrapper, from its own type-mapping source.
            property.SetProviderClrType(typeof(string));
        }

        return modelBuilder;
    }
}
