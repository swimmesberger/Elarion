namespace Elarion.AspNetCore;

/// <summary>
/// Place on the host assembly (<c>[assembly: GenerateModuleBootstrapper]</c>) to have the cross-module wiring
/// generated as the fixed-name <c>ElarionBootstrapper</c> static in the assembly's root namespace
/// (<c>AddElarion</c>, <c>MapElarionEndpoints</c>, <c>RegisterHandlers</c>, <c>GetMcpMetadata</c>, …), by
/// <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>. The type name is framework-owned for cross-project
/// consistency (see ADR-0018); you never declare it — to extend it, add your own <c>partial class
/// ElarionBootstrapper</c> in the root namespace.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GenerateModuleBootstrapperAttribute : Attribute;
