namespace Elarion.Abstractions;

/// <summary>
/// Triggers generation of the per-module permission-catalog registration methods that populate
/// <see cref="Authorization.IPermissionCatalog"/> from every <c>[RequirePermission]</c>/<c>[RequireRole]</c>
/// declared in the assembly. Place on the assembly: <c>[assembly: GeneratePermissionCatalog]</c>.
/// Use <see cref="UseElarionAttribute"/> to enable this together with the other assembly-level framework generators.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GeneratePermissionCatalogAttribute : Attribute;
