namespace Elarion.Abstractions.Settings;

/// <summary>
/// Marks a <c>static partial</c> class as a container of typed setting definitions. Every
/// <c>static partial</c> property of type <see cref="SettingDefinition{T}"/> annotated with
/// <see cref="SettingAttribute"/> is a declared setting: the source generator implements the property with the
/// key and metadata from the attribute, adds an <c>All</c> list of the class's definitions, and publishes the
/// definitions in the Elarion manifest so the host's <c>ElarionSettingDefinitions.All</c> aggregates them across
/// assemblies. See the settings concept page and ADR-0078.
/// </summary>
/// <example>
/// <code>
/// [SettingDefinitions]
/// public static partial class AppSettings {
///     [Setting("app:smtp", Pinnable = true, Description = "Outgoing mail server.")]
///     public static partial SettingDefinition&lt;SmtpSettings&gt; Smtp { get; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SettingDefinitionsAttribute : Attribute;
