using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared discovery of feature-flag declarations (<c>[FeatureFlag]</c> on a resolver class,
/// <c>[BackendFeatureFlag]</c> on any class) and flag usages (<c>[FeatureGate]</c>, <c>[FeatureVariant]</c>) for the
/// manifest generator (which publishes declarations) and the flag generator (which checks usages against the
/// union of local and referenced declarations, then registers and catalogs them), so the two cannot drift.
/// </summary>
internal static class FeatureFlagDiscovery {
    public const string FeatureFlagAttributeMetadataName = "Elarion.Abstractions.Features.FeatureFlagAttribute";

    public const string BackendFeatureFlagAttributeMetadataName =
        "Elarion.Abstractions.Features.BackendFeatureFlagAttribute";

    public const string FeatureGateAttributeMetadataName = "Elarion.Abstractions.Features.FeatureGateAttribute";
    public const string ResolverInterfaceMetadataName = "Elarion.Abstractions.Features.IFeatureFlagResolver";

    /// <summary>
    /// A flag declaration read from source. <see cref="ResolverProblem"/> is non-null when a code-defined flag's
    /// class cannot be its owner (not an <c>IFeatureFlagResolver</c>, abstract, or generic) — reported as ELFLAG003
    /// and never registered.
    /// </summary>
    public sealed record Declaration(
        string Namespace,
        string Name,
        string? Description,
        bool ExposeToClient,
        bool IsCode,
        string TypeFqn,
        string? ResolverProblem,
        LocationInfo Location) {
        public bool HasValidName => !string.IsNullOrWhiteSpace(Name);

        public ElarionManifest.FeatureFlag ToManifest() {
            return new ElarionManifest.FeatureFlag(Namespace, Name, Description, ExposeToClient, IsCode);
        }
    }

    /// <summary>One <c>[FeatureGate]</c>/<c>[FeatureVariant]</c> reference to a flag name, with where it was written.</summary>
    public sealed record Usage(string Name, string Attribute, LocationInfo Location);

    public static EquatableArray<Declaration> CreateDeclarations(GeneratorAttributeSyntaxContext ctx, bool isCode) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || ctx.Attributes.Length == 0)
            return EquatableArray<Declaration>.Empty;

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var resolverProblem = isCode ? FindResolverProblem(type, ctx.SemanticModel.Compilation) : null;

        var builder = ImmutableArray.CreateBuilder<Declaration>();
        foreach (var attribute in ctx.Attributes) {
            var name = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
                : string.Empty;
            string? description = null;
            var expose = false;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Description" && named.Value.Value is string d && !string.IsNullOrWhiteSpace(d))
                    description = d;
                else if (named.Key == "ExposeToClient" && named.Value.Value is bool e)
                    expose = e;

            builder.Add(new Declaration(
                ns, name, description, expose, isCode, typeFqn, resolverProblem,
                LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                  ?? type.Locations.FirstOrDefault())));
        }

        return builder.ToImmutable().ToEquatableArray();
    }

    public static EquatableArray<Usage> CreateGateUsages(GeneratorAttributeSyntaxContext ctx) {
        var builder = ImmutableArray.CreateBuilder<Usage>();
        foreach (var attribute in ctx.Attributes) {
            // The names are the trailing `params string[]` constructor argument, which Roslyn surfaces as one
            // array-kind constant regardless of which [FeatureGate] constructor was used.
            if (attribute.ConstructorArguments.Length == 0) continue;

            var featuresArg = attribute.ConstructorArguments[attribute.ConstructorArguments.Length - 1];
            if (featuresArg.Kind != TypedConstantKind.Array) continue;

            var location = AttributeLocation(attribute, ctx.TargetSymbol);
            foreach (var value in featuresArg.Values)
                if (value.Value is string name && !string.IsNullOrWhiteSpace(name))
                    builder.Add(new Usage(name, "FeatureGate", location));
        }

        return builder.ToImmutable().ToEquatableArray();
    }

    public static EquatableArray<Usage> CreateVariantUsages(GeneratorAttributeSyntaxContext ctx) {
        var builder = ImmutableArray.CreateBuilder<Usage>();
        foreach (var attribute in ctx.Attributes)
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string name &&
                !string.IsNullOrWhiteSpace(name))
                builder.Add(new Usage(name, "FeatureVariant", AttributeLocation(attribute, ctx.TargetSymbol)));

        return builder.ToImmutable().ToEquatableArray();
    }

    private static LocationInfo AttributeLocation(AttributeData attribute, ISymbol target) {
        return LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                                 ?? target.Locations.FirstOrDefault());
    }

    private static string? FindResolverProblem(INamedTypeSymbol type, Compilation compilation) {
        var resolver = compilation.GetTypeByMetadataName(ResolverInterfaceMetadataName);
        if (resolver is null || !type.AllInterfaces.Contains(resolver, SymbolEqualityComparer.Default))
            return "it does not implement IFeatureFlagResolver";

        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic)
            return "it is not a concrete (non-static, non-abstract) class";

        if (type.IsGenericType)
            return "a generic resolver cannot be registered";

        return null;
    }
}
