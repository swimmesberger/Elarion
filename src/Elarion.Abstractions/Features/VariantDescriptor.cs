namespace Elarion.Abstractions.Features;

/// <summary>
/// Compile-time-known description of one variant switch: the contract, its selection axis and key, the
/// declared variant values, and the default. The variant registry generator emits one per variant contract
/// into the assembly's <c>ElarionVariants</c> static (aggregated cross-assembly); the host seeds runtime
/// consumers explicitly via <c>AddElarionVariantCatalog(ElarionVariants.All)</c>, so application modules can
/// enumerate and validate against switches declared in assemblies they do not reference (platform adapters in
/// an infrastructure project).
/// </summary>
public sealed record VariantDescriptor {
    /// <summary>The selection axis — per-user feature allocation or a global configuration value.</summary>
    public required VariantAxis Axis { get; init; }

    /// <summary>The selector key: the configuration key (e.g. <c>"Email:Backend"</c>) or the feature name.</summary>
    public required string Key { get; init; }

    /// <summary>The variant contract's fully-qualified name — always present, for display and diagnostics.</summary>
    public required string ContractName { get; init; }

    /// <summary>
    /// The variant contract type, or <c>null</c> when the contract is not accessible from the assembly whose
    /// registry carried this descriptor (an internal contract in a referenced assembly — its switch still
    /// enumerates and validates; only type-based consumers, like the DI-registration startup check, skip it).
    /// </summary>
    public Type? Contract { get; init; }

    /// <summary>
    /// The declared variant values, ordinally sorted and including a named default — lower-cased on the
    /// configuration axis, exactly as matched at resolution time. An unnamed default is not listed (it has no
    /// selectable value); <see cref="HasDefault"/> still reports it.
    /// </summary>
    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>The named default's value, or <c>null</c> when the default is unnamed or absent.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Whether a default implementation exists (named or unnamed).</summary>
    public required bool HasDefault { get; init; }

    /// <summary>
    /// The owning module (longest-prefix namespace match), or <c>null</c> for a <i>platform</i> variant —
    /// an implementation living outside every module, the documented placement for infrastructure adapters.
    /// </summary>
    public string? Module { get; init; }
}
