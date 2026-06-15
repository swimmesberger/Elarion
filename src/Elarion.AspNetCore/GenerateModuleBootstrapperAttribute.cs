namespace Elarion.AspNetCore;

/// <summary>
/// Marks a partial class to have its module bootstrapper methods generated
/// by <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GenerateModuleBootstrapperAttribute : Attribute;
