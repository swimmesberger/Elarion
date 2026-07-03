using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of <c>[FeatureVariant]</c>/<c>[ConfigurationVariant]</c> declarations for the manifest and
/// variant-catalog generators: one <see cref="ElarionManifest.Variant"/> per service contract, mirroring the
/// registration generator's contract resolution so the registry and the DI registrations cannot drift. A
/// variant without <c>[Service]</c> contributes nothing here — <c>ELVAR007</c> is the registration generator's
/// report, and an unregistered variant must not appear in the registry.
/// </summary>
internal static class VariantDiscovery
{
    public const string FeatureVariantAttributeMetadataName = "Elarion.Abstractions.Features.FeatureVariantAttribute";
    public const string ConfigurationVariantAttributeMetadataName = "Elarion.Abstractions.Features.ConfigurationVariantAttribute";

    public static EquatableArray<ElarionManifest.Variant> CreateVariants(
        GeneratorAttributeSyntaxContext ctx, bool isConfiguration)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract || ctx.Attributes.Length == 0)
            return EquatableArray<ElarionManifest.Variant>.Empty;

        var attribute = ctx.Attributes[0];
        var selectorKey = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(selectorKey))
            return EquatableArray<ElarionManifest.Variant>.Empty;

        var serviceAttr = ServiceContractResolver.FindServiceAttribute(classSymbol);
        if (serviceAttr is null)
            return EquatableArray<ElarionManifest.Variant>.Empty;

        var variantPropertyName = isConfiguration ? "Value" : "Variant";
        string? value = null;
        var isDefaultFlag = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == variantPropertyName && named.Value.Value is string v && !string.IsNullOrWhiteSpace(v))
                value = v;

            if (named.Key == "IsDefault" && named.Value.Value is bool d)
                isDefaultFlag = d;
        }

        // Mirror the registration generator: configuration values are matched case-insensitively via
        // lower-cased DI keys, so the registry carries the lower-cased form the runtime actually matches.
        if (isConfiguration && value is not null)
            value = value.ToLowerInvariant();

        var ns = classSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;

        var contracts = ResolveContractSymbols(classSymbol, serviceAttr);
        var builder = ImmutableArray.CreateBuilder<ElarionManifest.Variant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            var contractFqn = contract.ToDisplayString(fmt);
            if (!seen.Add(contractFqn))
                continue;

            builder.Add(new ElarionManifest.Variant(
                ns,
                isConfiguration,
                selectorKey,
                contractFqn,
                value,
                value is null || isDefaultFlag,
                IsEffectivelyPublic(contract)));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<ITypeSymbol> ResolveContractSymbols(
        INamedTypeSymbol classSymbol, AttributeData serviceAttr)
    {
        var explicitContracts = ServiceContractResolver.GetExplicitContracts(serviceAttr);
        if (!explicitContracts.IsEmpty)
            return explicitContracts;

        if (classSymbol.Interfaces.Length > 0)
            return ImmutableArray<ITypeSymbol>.CastUp(classSymbol.Interfaces);

        return ImmutableArray.Create<ITypeSymbol>(classSymbol);
    }

    // Whether typeof(contract) compiles from another assembly: the type and every containing type are public.
    private static bool IsEffectivelyPublic(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }

        return true;
    }
}
