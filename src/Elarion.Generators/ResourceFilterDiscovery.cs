using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery for <c>[ResourceFilter&lt;TEntity&gt;]</c> specs, consumed by
/// <see cref="ElarionManifestGenerator"/> (publishing into the assembly manifest) and
/// <see cref="AppModuleDiscoveryGenerator"/> (registering specs declared in the bootstrapper compilation
/// itself), so the two discoveries cannot drift.
/// </summary>
internal static class ResourceFilterDiscovery {
    public static ElarionManifest.ResourceFilter? CreateResourceFilter(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol specType)
            return null;

        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].AttributeClass is not { TypeArguments.Length: 1 } attributeClass ||
            attributeClass.TypeArguments[0] is not INamedTypeSymbol entity)
            return null;

        var shared = false;
        foreach (var named in ctx.Attributes[0].NamedArguments)
            switch (named.Key) {
                case "Shared" when named.Value.Value is true:
                    shared = true;
                    break;
                // A building block of a composed rule: the application registers the composition, so the filter is
                // neither published in the manifest nor registered by the bootstrapper.
                case "Register" when named.Value.Value is false:
                    return null;
            }

        return new ElarionManifest.ResourceFilter(
            specType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            specType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            shared);
    }
}
