namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of the assembly's <c>ElarionFeatureFlags</c> registry — the compile-time catalog of every
/// declared feature flag (<see cref="Features.FeatureFlagAttribute"/>/<see cref="Features.BackendFeatureFlagAttribute"/>),
/// aggregated across referenced assemblies from the Elarion manifest: one typed <see cref="Features.FeatureFlagKey"/>
/// per flag, a <c>Names</c> class of <c>const string</c> names (usable in <c>[FeatureGate]</c>), and
/// <see cref="Features.FeatureFlagDescriptor"/> data. Place on the assembly: <c>[assembly: GenerateFeatureFlags]</c>.
/// Use <see cref="UseElarionAttribute"/> to enable this together with the other assembly-level framework
/// generators. Declaration checking (<c>ELFLAG001</c>–<c>ELFLAG005</c>) and per-module registration do not need
/// this trigger.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateFeatureFlagsAttribute : Attribute;
