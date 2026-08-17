using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Elarion.EntityFrameworkCore;

/// <summary>Opt-in model conventions for value shapes an application would otherwise hand-roll.</summary>
public static class ElarionModelBuilderExtensions {
    /// <summary>
    /// Stores enum properties as their <b>name</b> rather than their ordinal, unless the property already
    /// declares a conversion or a provider type.
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
    /// <b>Scope it.</b> Pass the assemblies that own your entities and only their entity types are rewritten.
    /// With <b>no</b> arguments the pass applies to <em>every</em> entity type in the model, including types
    /// mapped in by a third-party library or another Elarion package into the same <c>DbContext</c> — their
    /// enum columns silently change from integer to text, which is a schema change on someone else's model.
    /// The generated client-assigned-keys pass scopes itself the same way and for the same reason, so prefer
    /// the scoped call; the unscoped one is for a context whose model is entirely application-owned.
    /// </para>
    /// <para>
    /// It is a <b>post-pass</b>: call it at the end of <c>OnModelCreating</c>, after the entity configurations
    /// have run, so it sees the finished property set (navigation-discovered entities included). Nullable enums
    /// are covered. Any property that already has a value converter, a converter type, or an explicit provider
    /// CLR type is left alone — deliberate configuration always wins, so one enum stored as an ordinal opts out
    /// by saying so in its configuration.
    /// </para>
    /// <para>
    /// There is deliberately no options bag beyond the scope: the convention is the whole decision, and
    /// per-property deviation is already expressible in the property's own configuration.
    /// </para>
    /// <example>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder) {
    ///     base.OnModelCreating(modelBuilder);
    ///     ConfigureEntities(modelBuilder);              // generated
    ///     modelBuilder.UseElarionEnumStringConversions(typeof(Invoice).Assembly);
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="modelBuilder">The model to configure.</param>
    /// <param name="entityAssemblies">
    /// The assemblies owning the entity types to rewrite. Empty applies the pass to the whole model — see the
    /// blast radius above.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static ModelBuilder UseElarionEnumStringConversions(
        this ModelBuilder modelBuilder,
        params Assembly[] entityAssemblies) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(entityAssemblies);

        // Null means "no scope given" — the documented model-wide behavior — and is distinct from an empty set.
        var scope = entityAssemblies.Length == 0 ? null : new HashSet<Assembly>(entityAssemblies);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList()) {
            if (scope is not null && !scope.Contains(entityType.ClrType.Assembly))
                continue;

            foreach (var property in entityType.GetProperties().ToList()) {
                // Any conversion already in the model — explicit, data-annotated, or contributed by another
                // convention — is a deliberate storage choice for that property; never overwrite it. The
                // converter-type annotation is the HasConversion<TConverter>() form, which is not resolved into
                // a converter instance until the model is finalized, so it is read directly (as the generated
                // client-assigned-keys pass reads its store-default annotations).
                if (property.GetValueConverter() is not null ||
                    property.FindAnnotation("ValueConverterType") is not null ||
                    property.GetProviderClrType() is not null)
                    continue;

                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!clrType.IsEnum)
                    continue;

                // Setting the provider type (rather than a converter instance) is exactly what
                // HasConversion<string>() does: EF resolves the enum-to-string converter, including the
                // nullable wrapper, from its own type-mapping source.
                property.SetProviderClrType(typeof(string));
            }
        }

        return modelBuilder;
    }
}
