namespace Elarion.Abstractions.Features;

/// <summary>
/// Runtime-injectable view of the variant switches the application offers. Deliberately <b>host-seeded</b>,
/// never generator-registered: the host — the one assembly that references everything — passes the generated
/// compile-time registry in (<c>services.AddElarionVariantCatalog(ElarionVariants.All)</c>), and application
/// modules consume the <i>data</i> without referencing the assemblies that declare the implementations. This is
/// what lets an application design its own settings-change APIs over platform switches: a handler injects the
/// catalog, validates a requested value against <see cref="FindByKey"/>, and writes through its own
/// authorization/audit pipeline.
/// </summary>
public interface IVariantCatalog {
    /// <summary>Every seeded switch descriptor.</summary>
    IReadOnlyList<VariantDescriptor> All { get; }

    /// <summary>
    /// The descriptors bound to <paramref name="key"/> (matched case-insensitively; several contracts may share
    /// one switch key and are switched together). Empty when the key is not offered.
    /// </summary>
    IReadOnlyList<VariantDescriptor> FindByKey(string key);
}
