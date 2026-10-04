namespace Elarion.Abstractions.Settings;

/// <summary>
/// Triggers generation of the assembly's <c>ElarionSettingDefinitions</c> registry: every
/// <see cref="SettingAttribute"/> definition this assembly declares or references through the Elarion manifest.
/// The host seeds the runtime catalog from it: <c>services.AddElarionSettings(o =&gt; o.AddDefinitions(ElarionSettingDefinitions.All))</c>.
/// Place on the assembly: <c>[assembly: GenerateSettingDefinitionCatalog]</c>; <see cref="UseElarionAttribute"/>
/// enables it together with the other assembly-level generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateSettingDefinitionCatalogAttribute : Attribute;
