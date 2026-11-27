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

        var ns = specType.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        // Another assembly's bootstrapper can name only public types; a non-public filter (or entity) is registered
        // through a public registrar its own assembly's manifest generator emits.
        var registrar = IsPublic(specType) && IsPublic(entity)
            ? null
            : "global::" + (ns.Length == 0 ? string.Empty : ns + ".") + specType.Name + RegistrarSuffix;
        return new ElarionManifest.ResourceFilter(
            specType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            specType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            shared,
            registrar);
    }

    /// <summary>The suffix of the public registrar class emitted next to a non-public filter.</summary>
    public const string RegistrarSuffix = "ElarionRegistration";

    private static bool IsPublic(ITypeSymbol type) {
        if (type is INamedTypeSymbol { IsGenericType: true } generic && generic.TypeArguments.Any(a => !IsPublic(a)))
            return false;

        for (var current = type; current is not null; current = current.ContainingType)
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;

        return true;
    }
}
