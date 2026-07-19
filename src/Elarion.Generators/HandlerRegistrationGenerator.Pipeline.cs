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
    /// Builds the module [DecoratorList] and [ElarionAuthorizationDefaults] maps once per resolution pass, by
    /// re-resolving each discovered [AppModule] type from the CURRENT compilation (a symbol-table lookup per
    /// module — never a tree scan) and reading its attributes fresh. Reading fresh matters: the [DecoratorList]
    /// meta-attribute usually sits on a pipeline-attribute CLASS in another file, so a cached read against the
    /// module's own tree would go stale when that class changes.
    /// </summary>
    private static (List<(string Namespace, AttributeData DecoratorList)> DecoratorLists,
        List<(string Namespace, bool RequireAuthenticated)> AuthDefaults,
        List<string> AuditDefaultNamespaces,
        List<(string Namespace, int TelemetryMode)> TelemetryDefaults) BuildModuleMaps(
            Compilation compilation,
            EquatableArray<ModuleScanner.Module> modules,
            CancellationToken ct) {
        var decoratorLists = new List<(string, AttributeData)>();
        var authDefaults = new List<(string, bool)>();
        var auditDefaults = new List<string>();
        var telemetryDefaults = new List<(string, int)>();
        var decoratorListMeta = compilation.GetTypeByMetadataName(DecoratorListAttributeMetadataName);
        var defaultsAttr = compilation.GetTypeByMetadataName(AuthorizationDefaultsAttributeMetadataName);
        var auditDefaultsAttr = compilation.GetTypeByMetadataName(AuditDefaultsAttributeMetadataName);
        var telemetryAttr = compilation.GetTypeByMetadataName(HandlerTelemetryAttributeMetadataName);
        if (decoratorListMeta is null && defaultsAttr is null && auditDefaultsAttr is null && telemetryAttr is null)
            return (decoratorLists, authDefaults, auditDefaults, telemetryDefaults);

        // Deterministic order: modules sorted by namespace then name, matching ELMOD001's documented
        // "alphabetically first" ownership tie-break for modules sharing a namespace.
        foreach (var module in modules.OrderBy(static m => m.Namespace, StringComparer.Ordinal)
                     .ThenBy(static m => m.Name, StringComparer.Ordinal)) {
            ct.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(module.MetadataName) is not { } moduleSymbol)
                continue;

            var attributes = moduleSymbol.GetAttributes();
            if (decoratorListMeta is not null) {
                var decoratorList = FindDecoratorListFromPipelineAttributes(attributes, decoratorListMeta);
                if (decoratorList is not null)
                    decoratorLists.Add((module.Namespace, decoratorList));
            }

            if (defaultsAttr is not null) {
                var defaults = attributes
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, defaultsAttr));
                if (defaults is not null)
                    authDefaults.Add((module.Namespace, ReadRequireAuthenticated(defaults)));
            }

            if (auditDefaultsAttr is not null &&
                attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, auditDefaultsAttr)))
                auditDefaults.Add(module.Namespace);

            if (telemetryAttr is not null &&
                ReadTelemetryMode(attributes, telemetryAttr) is { } moduleMode)
                telemetryDefaults.Add((module.Namespace, moduleMode));
        }

        return (decoratorLists, authDefaults, auditDefaults, telemetryDefaults);
    }

    /// <summary>Reads the [HandlerTelemetry] mode from an attribute list; null when undeclared.</summary>
    private static int? ReadTelemetryMode(ImmutableArray<AttributeData> attributes, INamedTypeSymbol telemetryAttr) {
        foreach (var attribute in attributes) {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, telemetryAttr))
                continue;

            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int mode)
                return mode;
        }

        return null;
    }

    /// <summary>
    /// Resolves the effective [HandlerTelemetry] mode for one handler: the handler's own declaration wins,
    /// else the owning module's (longest namespace in scope), else the assembly's, else Full (0).
    /// </summary>
    private static int ResolveTelemetryMode(
        INamedTypeSymbol classSymbol,
        Compilation compilation,
        IReadOnlyList<(string Namespace, int TelemetryMode)> moduleTelemetryDefaults) {
        var telemetryAttr = compilation.GetTypeByMetadataName(HandlerTelemetryAttributeMetadataName);
        if (telemetryAttr is null)
            return 0;

        if (ReadTelemetryMode(classSymbol.GetAttributes(), telemetryAttr) is { } handlerMode)
            return handlerMode;

        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        int? bestMode = null;
        var bestNamespaceLength = -1;
        foreach (var (moduleNamespace, mode) in moduleTelemetryDefaults) {
            if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                moduleNamespace.Length <= bestNamespaceLength)
                continue;

            bestMode = mode;
            bestNamespaceLength = moduleNamespace.Length;
        }

        if (bestMode is { } moduleScoped)
            return moduleScoped;

        return ReadTelemetryMode(compilation.Assembly.GetAttributes(), telemetryAttr) ?? 0;
    }

    private static AttributeData? FindModuleDecoratorList(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists) {
        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        AttributeData? bestDecoratorList = null;
        var bestNamespaceLength = -1;

        foreach (var (moduleNamespace, decoratorList) in moduleDecoratorLists) {
            if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                moduleNamespace.Length <= bestNamespaceLength)
                continue;

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

            foreach (var metaAttr in attr.AttributeClass.GetAttributes())
                if (SymbolEqualityComparer.Default.Equals(metaAttr.AttributeClass, decoratorListMeta))
                    return metaAttr;
        }

        return null;
    }

    internal static readonly DiagnosticDescriptor NonPublicAppliesToPredicate = new(
        "ELPIPE001",
        "Decorator AppliesTo predicate must be public",
        "Decorator '{0}' declares an 'AppliesTo' predicate that is not public; make it a "
        + "'public static bool AppliesTo({1} handler)' so the generated registration can call it",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor UnsupportedAppliesToSignature = new(
        "ELPIPE002",
        "Decorator AppliesTo predicate has an unsupported signature",
        "Decorator '{0}' declares an 'AppliesTo' method with an unsupported signature; the only attachment "
        + "predicate is 'public static bool AppliesTo({1} handler)' "
        + "(use handler.RequestType for request-based checks)",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

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

            // A decorator may narrow attachment with a `public static bool AppliesTo(HandlerMetadata handler)`
            // predicate, called once at pipeline-build time (see ADR-0003). It expresses unions/negations a
            // `where` cannot, may use any logic (including reflection over the handler's attributes), and is the
            // same capability the framework's built-in decorators use.
            var predicate = DecoratorPredicate.Detect(definition, compilation, out var predicateLocation);
            if (predicate == DecoratorPredicate.Result.NotPublic) {
                diagnostics.Add(DiagnosticInfo.Create(
                    NonPublicAppliesToPredicate, predicateLocation, definition.Name, "HandlerMetadata"));
                continue;
            }

            if (predicate == DecoratorPredicate.Result.UnsupportedSignature) {
                diagnostics.Add(DiagnosticInfo.Create(
                    UnsupportedAppliesToSignature, predicateLocation, definition.Name, "HandlerMetadata"));
                continue;
            }

            var closedType = definition.Construct(requestType, responseType);
            var decoratorFqn = closedType.ToDisplayString(fmt);
            // The open generic definition for `typeof(Foo<,>)` — pipeline decorators are always arity-2
            // (IHandler<TRequest, TResponse>), so strip the closed type arguments and re-add the unbound `<,>`.
            var openGenericFqn = decoratorFqn.Substring(0, decoratorFqn.IndexOf('<')) + "<,>";

            builder.Add(new DecoratorInfo(
                decoratorFqn,
                openGenericFqn,
                GetDecoratorExtraDependencies(closedType, fmt),
                predicate == DecoratorPredicate.Result.Conditional));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<DecoratorDependency> GetDecoratorExtraDependencies(
        INamedTypeSymbol closedType,
        SymbolDisplayFormat fmt) {
        var extraDeps = ImmutableArray.CreateBuilder<DecoratorDependency>();
        var constructors = closedType.Constructors;
        if (constructors.Length == 0)
            return extraDeps.ToImmutable();

        foreach (var param in constructors[0].Parameters) {
            if (param.Type is INamedTypeSymbol paramType &&
                paramType.OriginalDefinition.ToDisplayString() == "Elarion.Abstractions.IHandler<TRequest, TResponse>")
                continue;

            // A HandlerMetadata parameter is supplied by the generator (with the concrete handler type),
            // not resolved from DI — this is what makes attribute-driven decorators position-independent.
            var isHandlerMetadata = param.Type.ToDisplayString() == HandlerMetadataTypeName;
            extraDeps.Add(new DecoratorDependency(param.Type.ToDisplayString(fmt), isHandlerMetadata));
        }

        return extraDeps.ToImmutable();
    }

    // A decorator is `Decorator<TRequest, TResponse>`; it applies to a handler only if the handler's request
    // (TRequest) and response (TResponse) satisfy the decorator's type-parameter constraints.
    private static bool SatisfiesConstraints(INamedTypeSymbol definition, ITypeSymbol requestType,
        ITypeSymbol responseType) {
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
            // Substitute the *self* parameter with the concrete argument so a self-referential constraint
            // like `where TResponse : IResultFailureFactory<TResponse>` is honored (it scopes a decorator to
            // Result-returning handlers). A constraint that references a *different* type parameter (e.g.
            // `where TRequest : IFoo<TResponse>`) can't be resolved here, so stay permissive and never drop it.
            var resolved = ResolveConstraint(constraint, parameter, argument);
            if (resolved is null)
                continue;
            if (!SatisfiesType(argument, resolved))
                return false;
        }

        return true;
    }

    // Replaces occurrences of the constrained type parameter (self) with the concrete argument inside the
    // constraint type. Returns null when the constraint references a *different* type parameter, which cannot
    // be resolved in this single-parameter check.
    private static ITypeSymbol? ResolveConstraint(
        ITypeSymbol constraint,
        ITypeParameterSymbol self,
        ITypeSymbol argument) {
        if (constraint is ITypeParameterSymbol)
            return SymbolEqualityComparer.Default.Equals(constraint, self) ? argument : null;

        if (constraint is INamedTypeSymbol { IsGenericType: true } named) {
            var resolvedArgs = new ITypeSymbol[named.TypeArguments.Length];
            for (var i = 0; i < named.TypeArguments.Length; i++) {
                var resolvedArg = ResolveConstraint(named.TypeArguments[i], self, argument);
                if (resolvedArg is null)
                    return null;
                resolvedArgs[i] = resolvedArg;
            }

            return named.OriginalDefinition.Construct(resolvedArgs);
        }

        return constraint;
    }

    private static bool SatisfiesType(ITypeSymbol argument, ITypeSymbol constraint) {
        if (constraint.TypeKind == TypeKind.Interface) {
            if (SymbolEqualityComparer.Default.Equals(argument, constraint))
                return true;
            foreach (var iface in argument.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(iface, constraint))
                    return true;

            return false;
        }

        for (var current = argument; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, constraint))
                return true;

        return false;
    }

    private static bool IsNamespaceInScope(string candidateNamespace, string scopeNamespace) {
        return candidateNamespace == scopeNamespace ||
               candidateNamespace.StartsWith(scopeNamespace + ".", StringComparison.Ordinal);
    }
}
