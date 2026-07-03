namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of the assembly's <c>ElarionVariants</c> registry — the compile-time catalog of every
/// <c>[FeatureVariant]</c>/<c>[ConfigurationVariant]</c> switch: per-switch accessor classes with the selector
/// key and value constants (usable in attributes like <c>[AllowedValues(...)]</c>) plus
/// <see cref="Features.VariantDescriptor"/> data (<c>All</c>/<c>ByKey</c>/<c>ByModule</c>/<c>Platform</c>),
/// aggregated across referenced assemblies from the Elarion manifest. The host typically seeds the runtime
/// catalog from it: <c>services.AddElarionVariantCatalog(ElarionVariants.All)</c>. Place on the assembly:
/// <c>[assembly: GenerateVariantCatalog]</c>. Use <see cref="UseElarionAttribute"/> to enable this together
/// with the other assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateVariantCatalogAttribute : Attribute;
