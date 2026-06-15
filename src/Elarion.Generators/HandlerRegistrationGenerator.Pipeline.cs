using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private static AttributeData? ResolveDecoratorListFromPipelineAttributes(
        INamedTypeSymbol classSymbol,
        Compilation compilation,
        CancellationToken ct) {
        var decoratorListMeta = compilation.GetTypeByMetadataName(DecoratorListAttributeMetadataName);
        if (decoratorListMeta is null)
            return null;

        return FindDecoratorListFromPipelineAttributes(classSymbol.GetAttributes(), decoratorListMeta)
            ?? FindModuleDecoratorListFromPipelineAttributes(classSymbol, compilation, decoratorListMeta, ct)
            ?? FindDecoratorListFromPipelineAttributes(compilation.Assembly.GetAttributes(), decoratorListMeta);
    }

    private static AttributeData? FindModuleDecoratorListFromPipelineAttributes(
        INamedTypeSymbol classSymbol,
        Compilation compilation,
        INamedTypeSymbol decoratorListMeta,
        CancellationToken ct) {
        var moduleAttrSymbol = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (moduleAttrSymbol is null)
            return null;

        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        AttributeData? bestDecoratorList = null;
        var bestNamespaceLength = -1;

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

                var moduleNamespace = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                    moduleNamespace.Length <= bestNamespaceLength) {
                    continue;
                }

                var decoratorList = FindDecoratorListFromPipelineAttributes(typeSymbol.GetAttributes(), decoratorListMeta);
                if (decoratorList is null)
                    continue;

                bestDecoratorList = decoratorList;
                bestNamespaceLength = moduleNamespace.Length;
            }
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

    private static ImmutableArray<DecoratorInfo> ParseDecorators(
        AttributeData decoratorListAttr,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
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

            var definition = openDecoratorType.IsUnboundGenericType
                ? openDecoratorType.OriginalDefinition
                : openDecoratorType;
            var closedType = definition.Construct(requestType, responseType);
            var decoratorFqn = closedType.ToDisplayString(fmt);

            builder.Add(new DecoratorInfo(decoratorFqn, GetDecoratorExtraDependencies(closedType, fmt)));
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

    private static bool IsNamespaceInScope(string candidateNamespace, string scopeNamespace) =>
        candidateNamespace == scopeNamespace ||
        candidateNamespace.StartsWith(scopeNamespace + ".", StringComparison.Ordinal);
}

