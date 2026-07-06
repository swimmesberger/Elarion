namespace Elarion.Abstractions;

/// <summary>
/// Enables generated client-event topic registration (a per-module <c>Add{Module}ClientEvents</c> for every
/// <c>IClientEvent</c> contract, wired into the module's <c>ConfigureDefaultServices</c>). Place on the
/// assembly: <c>[assembly: GenerateClientEventTopics]</c>. Also enabled by <see cref="UseElarionAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateClientEventTopicsAttribute : Attribute;
