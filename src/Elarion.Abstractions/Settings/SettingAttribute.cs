namespace Elarion.Abstractions.Settings;

/// <summary>
/// Declares one setting on a <c>static partial</c> property of type <see cref="SettingDefinition{T}"/> inside a
/// <see cref="SettingDefinitionsAttribute"/> class. The attribute carries the compile-time-verifiable metadata;
/// the generator emits the property implementation. The default value is either a constant
/// (<see cref="Default"/>) or a static factory method of the same class (<see cref="DefaultFactory"/>) — not both.
/// </summary>
/// <param name="key">
/// The hierarchical key (for example <c>"app:smtp:host"</c>), unique case-insensitively across the application so it
/// can also serve as the <c>IConfiguration</c> key. Must not be blank or start or end with <c>':'</c>.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string key) : Attribute {
    /// <summary>The hierarchical setting key.</summary>
    public string Key { get; } = key;

    /// <summary>
    /// The scope kinds the setting may be read and written in (<c>"global"</c>, <c>"user"</c>, …). Defaults to
    /// global only. Accessing the setting in another scope kind is refused.
    /// </summary>
    public string[]? Scopes { get; init; }

    /// <summary>
    /// Whether the value is a secret: protected at rest through the registered
    /// <c>ISettingValueProtector</c>, never returned by the describe API, and never projected into
    /// <c>IConfiguration</c>. A secret cannot declare a default.
    /// </summary>
    public bool Secret { get; init; }

    /// <summary>
    /// Whether deployment configuration may pin the value: when <c>IConfiguration</c> supplies a value for the
    /// key it wins over the stored value and writes are refused. Requires the global scope.
    /// </summary>
    public bool Pinnable { get; init; }

    /// <summary>A human-readable description for admin tooling.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// A constant default (string, bool, number, or enum value) for scalar settings. Mutually exclusive with
    /// <see cref="DefaultFactory"/>.
    /// </summary>
    public object? Default { get; init; }

    /// <summary>
    /// The name (use <c>nameof</c>) of a <c>static</c> parameterless method of the same class returning the
    /// setting's value type, for non-constant defaults such as records. Evaluated lazily, once.
    /// </summary>
    public string? DefaultFactory { get; init; }
}
