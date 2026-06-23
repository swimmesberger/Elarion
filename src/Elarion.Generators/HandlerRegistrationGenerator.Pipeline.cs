using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private static AttributeData? ResolveDecoratorListFromPipelineAttributes(
        INamedTypeSymbol classSymbol,
        Compilation compilation,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists) {
        var decoratorListMeta = compilation.GetTypeByMetadataName(DecoratorListAttributeMetadataName);
        if (decoratorListMeta is null)
            return null;

        return FindDecoratorListFromPipelineAttributes(classSymbol.GetAttributes(), decoratorListMeta)
            ?? FindModuleDecoratorList(classSymbol, moduleDecoratorLists)
            ?? FindDecoratorListFromPipelineAttributes(compilation.Assembly.GetAttributes(), decoratorListMeta);
    }

    /// <summary>
    /// Builds the module [DecoratorList] map once per resolution pass — the [AppModule] types that carry a
    /// pipeline attribute, with their namespace — so <see cref="FindModuleDecoratorList"/> is a longest-prefix
    /// lookup instead of a per-handler full-compilation scan.
    /// </summary>
    private static List<(string Namespace, AttributeData DecoratorList)> BuildModuleDecoratorMap(
        Compilation compilation,
        CancellationToken ct) {
        var result = new List<(string, AttributeData)>();
        var decoratorListMeta = compilation.GetTypeByMetadataName(DecoratorListAttributeMetadataName);
        var moduleAttrSymbol = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (decoratorListMeta is null || moduleAttrSymbol is null)
            return result;

        foreach (var syntaxTree in compilation.SyntaxTrees) {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot(ct);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>()) {
                if (semanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol)
                    continue;

                var hasAppModule = typeSymbol.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttrSymbol));
                if (!hasAppModule)
                    continue;

                var decoratorList = FindDecoratorListFromPipelineAttributes(typeSymbol.GetAttributes(), decoratorListMeta);
                if (decoratorList is null)
                    continue;

                var moduleNamespace = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
                    ? containing.ToDisplayString()
                    : "";
                result.Add((moduleNamespace, decoratorList));
            }
        }

        return result;
    }

    private static AttributeData? FindModuleDecoratorList(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists) {
        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        AttributeData? bestDecoratorList = null;
        var bestNamespaceLength = -1;

        foreach (var (moduleNamespace, decoratorList) in moduleDecoratorLists) {
            if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                moduleNamespace.Length <= bestNamespaceLength) {
                continue;
            }

            bestDecoratorList = decoratorList;
            bestNamespaceLength = moduleNamespace.Length;
        }

        return bestDecoratorList;
    }

    private static AttributeData? FindDecoratorListFromPipelineAttributes(
        ImmutableArray<AttributeData> attributes,
        INamedTypeSymbol decoratorListMeta) {
        foreach (var attr in attributes) {
            if (attr.AttributeClass is null)
                continue;

            foreach (var metaAttr in attr.AttributeClass.GetAttributes()) {
                if (SymbolEqualityComparer.Default.Equals(metaAttr.AttributeClass, decoratorListMeta))
                    return metaAttr;
            }
        }

        return null;
    }

    private static readonly DiagnosticDescriptor NonPublicAppliesToPredicate = new(
        "ELPIPE001",
        "Decorator AppliesTo predicate must be public",
        "Decorator '{0}' declares an 'AppliesTo' predicate that is not public; make it a "
        + "'public static bool AppliesTo(System.Type request)' so the generated registration can call it",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static ImmutableArray<DecoratorInfo> ParseDecorators(
        AttributeData decoratorListAttr,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        SymbolDisplayFormat fmt) {
        var builder = ImmutableArray.CreateBuilder<DecoratorInfo>();

        if (decoratorListAttr.ConstructorArguments.Length == 0)
            return builder.ToImmutable();

        var args = decoratorListAttr.ConstructorArguments[0];
        if (args.Kind != TypedConstantKind.Array)
            return builder.ToImmutable();

        foreach (var typeArg in args.Values) {
            if (typeArg.Value is not INamedTypeSymbol openDecoratorType)
                continue;

            var definition = openDecoratorType.OriginalDefinition;

            // Compile-time decorator filtering: skip a decorator whose generic constraints the handler's
            // request/response don't satisfy (e.g. `where TRequest : ICommand` on a query handler). This lets a
            // module-wide [DecoratorList] carry kind-specific decorators that only wrap the matching handlers.
            if (!SatisfiesConstraints(definition, requestType, responseType))
                continue;

            // A decorator may narrow attachment with a `public static bool AppliesTo(Type request)` predicate,
            // called once at pipeline-build time (see ADR-0003). It expresses unions/negations a `where` cannot,
            // and may use any logic — including reflection over the request type's attributes.
            var predicate = DecoratorPredicate.Detect(definition, compilation, out var predicateLocation);
            if (predicate == DecoratorPredicate.Result.NotPublic) {
                diagnostics.Add(DiagnosticInfo.Create(
                    NonPublicAppliesToPredicate, predicateLocation, definition.Name));
                continue;
            }

            var closedType = definition.Construct(requestType, responseType);
            var decoratorFqn = closedType.ToDisplayString(fmt);

            builder.Add(new DecoratorInfo(
                decoratorFqn,
                GetDecoratorExtraDependencies(closedType, fmt),
                predicate == DecoratorPredicate.Result.Conditional));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> GetDecoratorExtraDependencies(
        INamedTypeSymbol closedType,
        SymbolDisplayFormat fmt) {
        var extraDeps = ImmutableArray.CreateBuilder<string>();
        var constructors = closedType.Constructors;
        if (constructors.Length == 0)
            return extraDeps.ToImmutable();

        foreach (var param in constructors[0].Parameters) {
            if (param.Type is INamedTypeSymbol paramType &&
                paramType.OriginalDefinition.ToDisplayString() == "Elarion.Abstractions.IHandler<TRequest, TResponse>") {
                continue;
            }

            extraDeps.Add(param.Type.ToDisplayString(fmt));
        }

        return extraDeps.ToImmutable();
    }

    // A decorator is `Decorator<TRequest, TResponse>`; it applies to a handler only if the handler's request
    // (TRequest) and response (TResponse) satisfy the decorator's type-parameter constraints.
    private static bool SatisfiesConstraints(INamedTypeSymbol definition, ITypeSymbol requestType, ITypeSymbol responseType) {
        var typeParameters = definition.TypeParameters;
        if (typeParameters.Length != 2)
            return true; // Not the expected shape; stay permissive rather than drop the decorator.

        return SatisfiesParameter(typeParameters[0], requestType)
            && SatisfiesParameter(typeParameters[1], responseType);
    }

    private static bool SatisfiesParameter(ITypeParameterSymbol parameter, ITypeSymbol argument) {
        if (parameter.HasReferenceTypeConstraint && argument.IsValueType)
            return false;
        if (parameter.HasValueTypeConstraint && !argument.IsValueType)
            return false;

        foreach (var constraint in parameter.ConstraintTypes) {
            // A constraint that references another type parameter (e.g. `where TRequest : IFoo<TResponse>`) can't be
            // reliably substituted here; stay permissive so a valid decorator is never dropped.
            if (ReferencesTypeParameter(constraint))
                continue;
            if (!SatisfiesType(argument, constraint))
                return false;
        }

        return true;
    }

    private static bool SatisfiesType(ITypeSymbol argument, ITypeSymbol constraint) {
        if (constraint.TypeKind == TypeKind.Interface) {
            if (SymbolEqualityComparer.Default.Equals(argument, constraint))
                return true;
            foreach (var iface in argument.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(iface, constraint))
                    return true;
            }

            return false;
        }

        for (ITypeSymbol? current = argument; current is not null; current = current.BaseType) {
            if (SymbolEqualityComparer.Default.Equals(current, constraint))
                return true;
        }

        return false;
    }

    private static bool ReferencesTypeParameter(ITypeSymbol type) {
        if (type is ITypeParameterSymbol)
            return true;

        if (type is INamedTypeSymbol named) {
            foreach (var argument in named.TypeArguments) {
                if (ReferencesTypeParameter(argument))
                    return true;
            }
        }

        return false;
    }

    private static bool IsNamespaceInScope(string candidateNamespace, string scopeNamespace) =>
        candidateNamespace == scopeNamespace ||
        candidateNamespace.StartsWith(scopeNamespace + ".", StringComparison.Ordinal);
}

