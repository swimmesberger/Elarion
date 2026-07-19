using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared resolution of the service contracts a <c>[Service]</c> class registers under, so the service generator,
/// the variant generator, and the handler generator all agree on the rule without duplicating it: explicit
/// <c>[Service(typeof(...))]</c> types, else the directly-implemented interfaces, else the implementation type
/// itself. <c>[FeatureVariant]</c> is a modifier on <c>[Service]</c>, so it reuses exactly this set.
/// </summary>
internal static class ServiceContractResolver {
    public const string ServiceAttributeMetadataName = "Elarion.Abstractions.ServiceAttribute";

    /// <summary>The class's <c>[Service]</c> attribute, or <see langword="null"/> when it is not a service.</summary>
    public static AttributeData? FindServiceAttribute(INamedTypeSymbol classSymbol) {
        return classSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ServiceAttributeMetadataName);
    }

    /// <summary>The explicit <c>[Service(typeof(...))]</c> contract symbols (empty when none were supplied).</summary>
    public static ImmutableArray<ITypeSymbol> GetExplicitContracts(AttributeData serviceAttr) {
        if (serviceAttr.ConstructorArguments.Length == 0)
            return ImmutableArray<ITypeSymbol>.Empty;

        var firstArg = serviceAttr.ConstructorArguments[0];
        if (firstArg.Kind != TypedConstantKind.Array)
            return ImmutableArray<ITypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>();
        foreach (var value in firstArg.Values)
            if (value.Value is ITypeSymbol contract)
                builder.Add(contract);

        return builder.ToImmutable();
    }

    /// <summary>
    /// The fully-qualified contracts the service registers under, in declaration order, de-duplicated: explicit
    /// types if any, else directly-implemented interfaces, else the implementation type itself.
    /// </summary>
    public static ImmutableArray<string> ResolveContractFqns(
        INamedTypeSymbol classSymbol,
        AttributeData serviceAttr,
        SymbolDisplayFormat fmt) {
        var explicitContracts = GetExplicitContracts(serviceAttr);
        if (!explicitContracts.IsEmpty)
            return DistinctPreservingOrder(explicitContracts.Select(c => c.ToDisplayString(fmt)));

        var interfaces = classSymbol.Interfaces.Select(i => i.ToDisplayString(fmt)).ToList();
        if (interfaces.Count == 0)
            return ImmutableArray.Create(classSymbol.ToDisplayString(fmt));

        return DistinctPreservingOrder(interfaces);
    }

    public static ImmutableArray<string> DistinctPreservingOrder(IEnumerable<string> contracts) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var contract in contracts)
            if (seen.Add(contract))
                result.Add(contract);

        return result.ToImmutable();
    }
}
