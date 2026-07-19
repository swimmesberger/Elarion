using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared full-fidelity discovery for <c>[AppModule]</c> declarations (attribute arguments, convention-hook
/// probes, <c>[ClientFeatures]</c>) and <c>[ModuleEndpoints]</c> contributors, consumed by
/// <see cref="ElarionManifestGenerator"/> (publishing into the assembly manifest) and
/// <see cref="AppModuleDiscoveryGenerator"/> (reading the bootstrapper compilation itself), so the two
/// discoveries cannot drift. Distinct from <see cref="ModuleProviders"/>, whose lightweight
/// <c>ModuleScanner.Module</c> model (name + namespace only) serves the scope-matching generators.
/// </summary>
internal static class AppModuleDiscovery {
    /// <summary>Reads one <c>[AppModule]</c> declaration into the manifest module model.</summary>
    public static ElarionManifest.Module? CreateModule(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var clientFeaturesType =
            ctx.SemanticModel.Compilation.GetTypeByMetadataName(ElarionGeneratorConventions.ClientFeaturesAttribute);

        foreach (var attr in ctx.Attributes) {
            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not string moduleName)
                continue;

            string? dependsOn = null;
            var isCore = false;
            foreach (var named in attr.NamedArguments)
                if (named.Key == "DependsOn" && named.Value.Value is string deps)
                    dependsOn = deps;
                else if (named.Key == "Kind")
                    isCore = IsCoreModuleKind(named.Value);

            return new ElarionManifest.Module(
                moduleName,
                type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                dependsOn,
                isCore,
                HasStaticMethod(type, "ConfigureServices", 2),
                HasStaticMethod(type, "MapEndpoints", 1),
                HasStaticMethod(type, "GetJsonTypeInfoResolver", 0),
                HasStaticMethod(type, "ConfigureEndpointGroup", 1),
                ReadClientFeatures(type, clientFeaturesType));
        }

        return null;
    }

    /// <summary>
    /// Reads one <c>[ModuleEndpoints]</c> contributor: the named module plus which convention hooks the class
    /// declares. Hook-less contributors are still returned (both flags <c>false</c>) so each caller applies its
    /// own policy — the manifest generator reports ELMOD005, the bootstrapper drops them silently.
    /// </summary>
    public static ElarionManifest.ModuleEndpoints? CreateModuleEndpoints(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        foreach (var attr in ctx.Attributes) {
            if (attr.ConstructorArguments.Length == 0 ||
                attr.ConstructorArguments[0].Value is not string moduleName ||
                moduleName.Length == 0)
                continue;

            return new ElarionManifest.ModuleEndpoints(
                moduleName,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                HasStaticMethod(type, "MapEndpoints", 1),
                HasStaticMethod(type, "ConfigureEndpointGroup", 1));
        }

        return null;
    }

    /// <summary>Reads the names listed by a module's <c>[ClientFeatures(...)]</c> attribute (empty when absent).</summary>
    private static EquatableArray<string> ReadClientFeatures(INamedTypeSymbol type,
        INamedTypeSymbol? clientFeaturesType) {
        if (clientFeaturesType is null)
            return EquatableArray<string>.Empty;

        foreach (var attr in type.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, clientFeaturesType))
                continue;
            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Kind != TypedConstantKind.Array)
                return EquatableArray<string>.Empty;

            var names = new List<string>();
            foreach (var value in attr.ConstructorArguments[0].Values)
                if (value.Value is string name && name.Length > 0)
                    names.Add(name);

            return names.ToEquatableArray();
        }

        return EquatableArray<string>.Empty;
    }

    private static bool HasStaticMethod(INamedTypeSymbol type, string name, int paramCount) {
        foreach (var member in type.GetMembers(name))
            if (member is IMethodSymbol { IsStatic: true } method && method.Parameters.Length == paramCount)
                return true;

        return false;
    }

    private static bool IsCoreModuleKind(TypedConstant value) {
        if (value.Type is not INamedTypeSymbol enumType)
            return false;

        foreach (var member in enumType.GetMembers("Core"))
            if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, value.Value))
                return true;

        return false;
    }
}
