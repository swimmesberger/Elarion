using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the DI registration and independent decorator pipeline for request-driven stream handlers.
/// Stream handlers deliberately share pipeline selection rules with unary handlers, while shape-filtering a
/// mixed <c>DecoratorList</c> so unary-only decorators never affect a stream.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class StreamHandlerRegistrationGenerator : IIncrementalGenerator {
    private const string StreamInterface = "Elarion.Abstractions.IStreamHandler<TRequest, TItem>";
    private const string StreamInterfaceMetadata = "Elarion.Abstractions.IStreamHandler`2";
    private const string DecoratorListMetadata = "Elarion.Abstractions.Pipeline.DecoratorListAttribute";
    private const string StreamMetadata = "Elarion.Abstractions.Pipeline.StreamHandlerMetadata";
    private const string Trigger = "Elarion.Abstractions.GenerateModuleHandlersAttribute";
    private const string ValidationExtensions = "Elarion.Validation.ElarionValidationServiceCollectionExtensions";

    private const string AuthorizationDefaults =
        "Elarion.Abstractions.Authorization.ElarionAuthorizationDefaultsAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => Candidate(ctx, ct))
            .Where(static name => name is not null)
            .WithTrackingName("StreamHandlerCandidateNodes")
            .Collect()
            .Select(static (names, _) => names.Where(static x => x is not null).Select(static x => x!)
                .Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToImmutableArray()
                .ToEquatableArray())
            .WithTrackingName("StreamHandlerCandidates");
        var modules = ModuleProviders.CollectModules(context);
        var models = candidates.Combine(modules).Combine(context.CompilationProvider)
            .Select(static (source, ct) => Resolve(source.Left.Left, source.Left.Right, source.Right, ct))
            .WithTrackingName("StreamHandlers");

        context.RegisterSourceOutput(models, static (spc, handlers) => {
            foreach (var handler in handlers) {
                foreach (var diagnostic in handler.Diagnostics)
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                spc.AddSource($"{HintNames.Sanitize(handler.HandlerFqn)}.stream.g.cs",
                    SourceText.From(Emit(handler), Encoding.UTF8));
            }
        });

        var aggregation = models.Combine(modules).Combine(ModuleProviders.HasTrigger(context, Trigger))
            .WithTrackingName("StreamHandlerModuleAggregation");
        context.RegisterSourceOutput(aggregation, static (spc, source) => {
            var ((handlers, allModules), enabled) = source;
            if (enabled)
                EmitModuleAggregations(spc, handlers, allModules);
        });
    }

    private static string? Candidate(GeneratorSyntaxContext context, CancellationToken ct) {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type || type.IsAbstract ||
            type.IsGenericType)
            return null;
        if (type.ContainingNamespace.ToDisplayString() == "Elarion.Pipeline")
            return null;
        return FindStream(type) is null ? null : ModuleScanner.BuildMetadataName(type);
    }

    private static EquatableArray<StreamInfo> Resolve(EquatableArray<string> names,
        EquatableArray<ModuleScanner.Module> modules, Compilation compilation, CancellationToken ct) {
        var format = SymbolDisplayFormat.FullyQualifiedFormat;
        var maps = BuildModuleDecoratorLists(compilation, modules, ct);
        var validationWalk = compilation.GetTypeByMetadataName(ValidationExtensions) is null
            ? null
            : new ValidatableTypeWalker.Context(compilation.Assembly);
        var result = ImmutableArray.CreateBuilder<StreamInfo>();
        foreach (var name in names) {
            ct.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(name) is not { } type || FindStream(type) is not { } stream)
                continue;
            var request = stream.TypeArguments[0];
            var item = stream.TypeArguments[1];
            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
            var classDeclaration = type.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(ct))
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();
            var resources = classDeclaration is null
                ? EquatableArray<ResourceBinding>.Empty
                : HandlerRegistrationGenerator.BuildResourceBindings(
                        classDeclaration, type, request,
                        EnumerateRequireResources(type), diagnostics)
                    .Select(static binding => new ResourceBinding(binding.ResourceTypeFqn, binding.Operation,
                        binding.IdPath, binding.ResourceTypeName))
                    .ToImmutableArray().ToEquatableArray();
            result.Add(new StreamInfo(
                type.ToDisplayString(format), type.Name, type.ContainingNamespace.ToDisplayString(),
                request.ToDisplayString(format), item.ToDisplayString(format),
                ParseDecorators(type, request, item, compilation, maps, format, diagnostics),
                ParseAuthorization(type, compilation, modules, out var requireAuthenticatedByDefault),
                requireAuthenticatedByDefault,
                resources,
                ParseFeatureGates(type, diagnostics),
                validationWalk is not null && ValidatableTypeWalker.IsValidatable(request, validationWalk),
                diagnostics.ToImmutable().ToEquatableArray()));
        }

        return result.ToImmutable().OrderBy(static h => h.HandlerFqn, StringComparer.Ordinal).ToEquatableArray();
    }

    private static bool HasAttribute(INamedTypeSymbol type, params string[] names) {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetAttributes().Any(a =>
                    a.AttributeClass is not null &&
                    names.Contains(a.AttributeClass.ToDisplayString(), StringComparer.Ordinal)))
                return true;
        return false;
    }

    private static List<AttributeData> EnumerateRequireResources(INamedTypeSymbol type) {
        var resources = new List<AttributeData>();
        for (var current = type; current is not null; current = current.BaseType)
            resources.AddRange(current.GetAttributes().Where(attribute =>
                attribute.AttributeClass?.ToDisplayString() ==
                "Elarion.Abstractions.Authorization.RequireResourceAttribute"));
        return resources;
    }

    private static bool ResolveAuthDefault(INamedTypeSymbol type, Compilation compilation,
        EquatableArray<ModuleScanner.Module> modules) {
        if (HasAttribute(type, "Elarion.Abstractions.Authorization.AllowAnonymousAttribute")) return false;
        var attribute = compilation.GetTypeByMetadataName(AuthorizationDefaults);
        if (attribute is null) return false;
        AttributeData? selected = null;
        var best = -1;
        foreach (var module in modules) {
            if (!ModuleScanner.IsInScope(type.ContainingNamespace.ToDisplayString(), module.Namespace) ||
                module.Namespace.Length <= best) continue;
            if (compilation.Assembly.GetTypeByMetadataName(module.MetadataName) is { } symbol) {
                var found = symbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
                if (found is not null) {
                    selected = found;
                    best = module.Namespace.Length;
                }
            }
        }

        selected ??= compilation.Assembly.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
        return selected?.NamedArguments.FirstOrDefault(p => p.Key == "RequireAuthenticated").Value.Value as bool? ??
               true;
    }

    // Keep the attachment decision aligned with the unary ParseAuthorization path. In particular,
    // AllowAnonymous is inherited and wins over both inherited explicit requirements and module/assembly defaults;
    // emitting the decorator in that case would needlessly require IAuthorizer from DI for a public stream.
    private static bool ParseAuthorization(INamedTypeSymbol type, Compilation compilation,
        EquatableArray<ModuleScanner.Module> modules, out bool requireAuthenticatedByDefault) {
        var allowAnonymous = HasAttribute(type, "Elarion.Abstractions.Authorization.AllowAnonymousAttribute");
        requireAuthenticatedByDefault = !allowAnonymous && ResolveAuthDefault(type, compilation, modules);
        if (allowAnonymous)
            return false;

        return HasAttribute(type,
                   "Elarion.Abstractions.Authorization.RequirePermissionAttribute",
                   "Elarion.Abstractions.Authorization.RequireRoleAttribute",
                   "Elarion.Abstractions.Authorization.RequireClaimAttribute",
                   "Elarion.Abstractions.Authorization.RequirePolicyAttribute",
                   "Elarion.Abstractions.Authorization.RequireResourceAttribute") ||
               requireAuthenticatedByDefault;
    }

    // A malformed/empty gate is inert at runtime just as it is for unary handlers, but it remains a build-time
    // warning. Do not add StreamFeatureGateDecorator when every declared gate is inert: that avoids a needless
    // IFeatureFlagService requirement and preserves the unary no-op contract.
    private static bool ParseFeatureGates(INamedTypeSymbol type, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var gates = new List<AttributeData>();
        for (var current = type; current is not null; current = current.BaseType)
            gates.AddRange(current.GetAttributes().Where(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Elarion.Abstractions.Features.FeatureGateAttribute"));

        var hasEffectiveGate = false;
        foreach (var gate in gates) {
            if (HandlerRegistrationGenerator.HasBlankFeatureName(gate))
                diagnostics.Add(DiagnosticInfo.Create(
                    HandlerRegistrationGenerator.EmptyFeatureGateDescriptor,
                    LocationInfo.From(type), type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            if (HandlerRegistrationGenerator.HasEffectiveFeatureName(gate)) hasEffectiveGate = true;
        }

        return hasEffectiveGate;
    }

    private static INamedTypeSymbol? FindStream(INamedTypeSymbol type) {
        return type.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == StreamInterface);
    }

    private static List<(string Namespace, AttributeData Attribute)> BuildModuleDecoratorLists(Compilation compilation,
        EquatableArray<ModuleScanner.Module> modules, CancellationToken ct) {
        var decoratorList = compilation.GetTypeByMetadataName(DecoratorListMetadata);
        var result = new List<(string, AttributeData)>();
        if (decoratorList is null) return result;
        foreach (var module in modules) {
            ct.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(module.MetadataName) is not { } type) continue;
            var attr = FindPipelineDecoratorList(type.GetAttributes(), decoratorList);
            if (attr is not null) result.Add((module.Namespace, attr));
        }

        return result;
    }

    private static AttributeData? ResolveDecoratorList(INamedTypeSymbol type, Compilation compilation,
        List<(string Namespace, AttributeData Attribute)> modules) {
        var decoratorList = compilation.GetTypeByMetadataName(DecoratorListMetadata);
        if (decoratorList is null) return null;
        var own = FindPipelineDecoratorList(type.GetAttributes(), decoratorList);
        if (own is not null) return own;
        var ns = type.ContainingNamespace.ToDisplayString();
        AttributeData? module = null;
        var length = -1;
        foreach (var candidate in modules)
            if (ModuleScanner.IsInScope(ns, candidate.Namespace) && candidate.Namespace.Length > length) {
                module = candidate.Attribute;
                length = candidate.Namespace.Length;
            }

        return module ?? FindPipelineDecoratorList(compilation.Assembly.GetAttributes(), decoratorList);
    }

    private static AttributeData? FindPipelineDecoratorList(ImmutableArray<AttributeData> attributes,
        INamedTypeSymbol decoratorList) {
        foreach (var attribute in attributes)
            if (attribute.AttributeClass is not null)
                foreach (var meta in attribute.AttributeClass.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(meta.AttributeClass, decoratorList))
                        return meta;
        return null;
    }

    private static EquatableArray<Decorator> ParseDecorators(INamedTypeSymbol handler, ITypeSymbol request,
        ITypeSymbol item, Compilation compilation, List<(string Namespace, AttributeData Attribute)> modules,
        SymbolDisplayFormat format, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var list = ResolveDecoratorList(handler, compilation, modules);
        var result = ImmutableArray.CreateBuilder<Decorator>();
        if (list?.ConstructorArguments.FirstOrDefault().Values is not { } values)
            return result.ToImmutable().ToEquatableArray();
        foreach (var value in values) {
            if (value.Value is not INamedTypeSymbol open || open.TypeParameters.Length != 2) continue;
            var definition = open.OriginalDefinition;
            if (!SatisfiesConstraints(definition, request, item, compilation)) continue;
            INamedTypeSymbol closed;
            try {
                closed = definition.Construct(request, item);
            }
            catch (ArgumentException) {
                continue;
            }

            var ctor = closed.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0 &&
                            IsStream(c.Parameters[0].Type))
                .OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
            if (ctor is null ||
                !closed.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == StreamInterface)) continue;
            var dependencies = ImmutableArray.CreateBuilder<Dependency>();
            for (var i = 1; i < ctor.Parameters.Length; i++) {
                var dependency = ctor.Parameters[i].Type.ToDisplayString(format);
                dependencies.Add(
                    new Dependency(dependency, ctor.Parameters[i].Type.ToDisplayString() == StreamMetadata));
            }

            var predicate = DecoratorPredicate.Detect(definition, compilation, StreamMetadata, out var location);
            if (predicate == DecoratorPredicate.Result.NotPublic) {
                diagnostics.Add(DiagnosticInfo.Create(HandlerRegistrationGenerator.NonPublicAppliesToPredicate,
                    location, definition.Name, "StreamHandlerMetadata"));
                continue;
            }

            if (predicate == DecoratorPredicate.Result.UnsupportedSignature) {
                diagnostics.Add(DiagnosticInfo.Create(HandlerRegistrationGenerator.UnsupportedAppliesToSignature,
                    location, definition.Name, "StreamHandlerMetadata"));
                continue;
            }

            result.Add(new Decorator(closed.ToDisplayString(format), OpenName(closed.ToDisplayString(format)),
                dependencies.ToImmutable().ToEquatableArray(), predicate == DecoratorPredicate.Result.Conditional));
        }

        return result.ToImmutable().ToEquatableArray();
    }

    private static bool IsStream(ITypeSymbol type) {
        return type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == StreamInterface;
    }

    private static string OpenName(string closed) {
        return closed.Substring(0, closed.IndexOf('<')) + "<,>";
    }

    private static bool SatisfiesConstraints(INamedTypeSymbol definition, ITypeSymbol request, ITypeSymbol item,
        Compilation compilation) {
        if (definition.TypeParameters.Length != 2) return false;
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default) {
            [definition.TypeParameters[0]] = request,
            [definition.TypeParameters[1]] = item
        };
        return Satisfies(definition.TypeParameters[0], request, substitutions, compilation) &&
               Satisfies(definition.TypeParameters[1], item, substitutions, compilation);
    }

    private static bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol argument,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> substitutions, Compilation compilation) {
        if (parameter.HasReferenceTypeConstraint && argument.IsValueType) return false;
        if (parameter.HasValueTypeConstraint && (!argument.IsValueType || IsNullableValueType(argument))) return false;
        if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType) return false;
        if (parameter.HasNotNullConstraint && argument.NullableAnnotation == NullableAnnotation.Annotated) return false;
        if (parameter.HasConstructorConstraint && !HasPublicParameterlessConstructor(argument)) return false;
        foreach (var constraint in parameter.ConstraintTypes)
            if (ResolveConstraint(constraint, substitutions, compilation) is not { } resolved ||
                !SatisfiesType(argument, resolved))
                return false;
        return true;
    }

    private static ITypeSymbol? ResolveConstraint(ITypeSymbol constraint,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> substitutions, Compilation compilation) {
        if (constraint is ITypeParameterSymbol parameter)
            return substitutions.TryGetValue(parameter, out var argument) ? argument : null;
        if (constraint is INamedTypeSymbol { IsGenericType: true } named) {
            var arguments = ImmutableArray.CreateBuilder<ITypeSymbol>(named.TypeArguments.Length);
            foreach (var typeArgument in named.TypeArguments) {
                if (ResolveConstraint(typeArgument, substitutions, compilation) is not { } resolved) return null;
                arguments.Add(resolved);
            }

            return named.OriginalDefinition.Construct(arguments.ToImmutable().ToArray());
        }

        if (constraint is IArrayTypeSymbol array && ResolveConstraint(array.ElementType, substitutions, compilation) is
                { } element)
            return compilation.CreateArrayTypeSymbol(element, array.Rank);
        return constraint;
    }

    private static bool SatisfiesType(ITypeSymbol argument, ITypeSymbol constraint) {
        if (constraint.TypeKind == TypeKind.Interface)
            return SymbolEqualityComparer.Default.Equals(argument, constraint) ||
                   argument.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, constraint));
        for (var current = argument; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, constraint))
                return true;
        return false;
    }

    private static bool IsNullableValueType(ITypeSymbol type) {
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    private static bool HasPublicParameterlessConstructor(ITypeSymbol type) {
        if (type.IsValueType) return !IsNullableValueType(type);
        return type is INamedTypeSymbol { IsAbstract: false } named && named.InstanceConstructors.Any(ctor =>
            ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 0);
    }

    private static string Emit(StreamInfo handler) {
        var sb = new StringBuilder();
        var iface = $"global::Elarion.Abstractions.IStreamHandler<{handler.RequestFqn}, {handler.ItemFqn}>";
        sb.AppendLine("// <auto-generated/>").AppendLine("#nullable enable")
            .AppendLine("using Microsoft.Extensions.DependencyInjection;").AppendLine();
        sb.AppendLine($"namespace {handler.Namespace};").AppendLine();
        sb.AppendLine($"public static class {handler.Name}StreamRegistration {{");
        sb.AppendLine(
            "    private static volatile global::System.Collections.Generic.IReadOnlyList<global::Elarion.Abstractions.Pipeline.PipelineStep>? __pipeline;");
        sb.AppendLine(
            "    private static readonly global::Elarion.Abstractions.Pipeline.StreamHandlerMetadata __metadata =");
        sb.AppendLine(
            $"        new(typeof({handler.HandlerFqn}), typeof({handler.RequestFqn}), typeof({handler.ItemFqn}), static () => __pipeline ?? global::System.Array.Empty<global::Elarion.Abstractions.Pipeline.PipelineStep>());");
        for (var i = 0; i < handler.Decorators.Count; i++)
            if (handler.Decorators[i].Conditional)
                sb.AppendLine(
                    $"    private static readonly bool __applies{i} = {handler.Decorators[i].Fqn}.AppliesTo(__metadata);");
        sb.AppendLine(
            $"    public static IServiceCollection Add{handler.Name}Stream(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped) {{");
        AppendConcreteRegistration(sb, handler.HandlerFqn);
        sb.AppendLine(
            $"        services.Add(new ServiceDescriptor(typeof({iface}), sp => Build(sp), __handlerLifetime));");
        sb.AppendLine("        return services; }");
        sb.AppendLine($"    private static {iface} Build(global::System.IServiceProvider sp) {{");
        sb.AppendLine($"        {iface} handler = sp.GetRequiredService<{handler.HandlerFqn}>();");
        sb.AppendLine(
            "        var steps = __pipeline is null ? new global::System.Collections.Generic.List<global::Elarion.Abstractions.Pipeline.PipelineStep>() : null;");
        for (var i = handler.Decorators.Count - 1; i >= 0; i--) {
            var d = handler.Decorators[i];
            if (d.Conditional) sb.AppendLine($"        if (__applies{i}) {{");
            sb.Append($"        handler = new {d.Fqn}(handler");
            foreach (var dep in d.Dependencies)
                sb.Append(dep.IsMetadata ? ", __metadata" : $", sp.GetRequiredService<{dep.Fqn}>()");
            sb.AppendLine(");");
            sb.AppendLine(
                $"        steps?.Add(new global::Elarion.Abstractions.Pipeline.PipelineStep(typeof({d.OpenFqn}), {d.Conditional.ToString().ToLowerInvariant()}));");
            if (d.Conditional) sb.AppendLine("        }");
        }

        if (handler.HasValidation) {
            sb.AppendLine(
                $"        handler = new global::Elarion.Pipeline.StreamValidationDecorator<{handler.RequestFqn}, {handler.ItemFqn}>(handler, sp.GetRequiredService<global::Elarion.Abstractions.Validation.IRequestValidator>());");
            sb.AppendLine(
                "        steps?.Add(new(typeof(global::Elarion.Pipeline.StreamValidationDecorator<,>), false));");
        }

        if (handler.HasFeature) {
            sb.AppendLine(
                $"        handler = new global::Elarion.Pipeline.StreamFeatureGateDecorator<{handler.RequestFqn}, {handler.ItemFqn}>(handler, __metadata, sp.GetRequiredService<global::Elarion.Abstractions.Features.IFeatureFlagService>());");
            sb.AppendLine(
                "        steps?.Add(new(typeof(global::Elarion.Pipeline.StreamFeatureGateDecorator<,>), false));");
        }

        if (handler.HasAuthorization) {
            sb.AppendLine(
                $"        handler = new global::Elarion.Pipeline.StreamAuthorizationDecorator<{handler.RequestFqn}, {handler.ItemFqn}>(handler, __metadata, sp.GetRequiredService<global::Elarion.Abstractions.Authorization.IAuthorizer>(), {handler.RequireAuthenticatedByDefault.ToString().ToLowerInvariant()}, new global::Elarion.Abstractions.Authorization.ResourceRequirementBinding<{handler.RequestFqn}>[] {{");
            foreach (var resource in handler.Resources)
                sb.AppendLine(
                    $"            new(typeof({resource.ResourceTypeFqn}), new global::Elarion.Abstractions.Authorization.ResourceOperation({HandlerRegistrationGenerator.FormatStringLiteral(resource.Operation)}), static request => request.{resource.IdPath}, {(resource.ResourceTypeName is null ? "null" : HandlerRegistrationGenerator.FormatStringLiteral(resource.ResourceTypeName))}),");
            sb.AppendLine("        });");
            sb.AppendLine(
                "        steps?.Add(new(typeof(global::Elarion.Pipeline.StreamAuthorizationDecorator<,>), false));");
        }

        sb.AppendLine(
            $"        handler = new global::Elarion.Pipeline.StreamObservabilityDecorator<{handler.RequestFqn}, {handler.ItemFqn}>(handler, \"{handler.Name}\", __metadata, sp.GetServices<global::Elarion.Abstractions.Diagnostics.IHandlerContextEnricher>(), sp.GetService<global::Microsoft.Extensions.Logging.ILoggerFactory>());");
        sb.AppendLine(
            "        steps?.Add(new(typeof(global::Elarion.Pipeline.StreamObservabilityDecorator<,>), false));");
        sb.AppendLine("        if (steps is not null) { steps.Reverse(); __pipeline = steps; } return handler; }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendConcreteRegistration(StringBuilder sb, string handlerFqn) {
        // Unary and stream registration can both target the same concrete class. Preserve the first concrete
        // registration's lifetime, so direct calls cannot become order-dependent last-registration-wins wiring.
        sb.AppendLine("        var __handlerLifetime = lifetime;");
        sb.AppendLine("        var __hasConcreteRegistration = false;");
        sb.AppendLine("        foreach (var descriptor in services) {");
        sb.AppendLine(
            $"            if (descriptor.IsKeyedService || descriptor.ServiceType != typeof({handlerFqn})) continue;");
        sb.AppendLine("            __handlerLifetime = descriptor.Lifetime;");
        sb.AppendLine("            __hasConcreteRegistration = true;");
        sb.AppendLine("            break;");
        sb.AppendLine("        }");
        sb.AppendLine(
            $"        if (!__hasConcreteRegistration) services.Add(new ServiceDescriptor(typeof({handlerFqn}), typeof({handlerFqn}), __handlerLifetime));");
    }

    private static void EmitModuleAggregations(SourceProductionContext spc, EquatableArray<StreamInfo> handlers,
        EquatableArray<ModuleScanner.Module> modules) {
        foreach (var module in modules.OrderBy(static m => m.Name, StringComparer.Ordinal)) {
            var entries = handlers.Where(h => Equals(ModuleScanner.FindBest(h.Namespace, modules), module))
                .OrderBy(static h => h.HandlerFqn, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) continue;
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>").AppendLine("#nullable enable")
                .AppendLine("using Microsoft.Extensions.DependencyInjection;").AppendLine();
            sb.AppendLine($"namespace {module.Namespace};").AppendLine();
            sb.AppendLine($"public static class {module.Name}StreamHandlerExtensions {{");
            sb.AppendLine(
                $"    public static IServiceCollection Add{module.Name}StreamHandlers(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped) {{");
            foreach (var entry in entries)
                sb.AppendLine(
                    $"        {entry.Namespace}.{entry.Name}StreamRegistration.Add{entry.Name}Stream(services, lifetime);");
            sb.AppendLine("        return services; }\n}");
            spc.AddSource($"{HintNames.Sanitize(module.Namespace + "." + module.Name)}.StreamHandlerExtensions.g.cs",
                SourceText.From(sb.ToString(), Encoding.UTF8));
            var prefix = module.Namespace.Length == 0 ? "global::" : $"global::{module.Namespace}.";
            ModuleDefaultsEmitter.EmitFiller(spc, module.Namespace, module.TypeName,
                ModuleDefaultsEmitter.AddStreamHandlersMethod, "StreamHandlers",
                $"{prefix}{module.Name}StreamHandlerExtensions.Add{module.Name}StreamHandlers(services);");
        }
    }

    private sealed record StreamInfo(
        string HandlerFqn,
        string Name,
        string Namespace,
        string RequestFqn,
        string ItemFqn,
        EquatableArray<Decorator> Decorators,
        bool HasAuthorization,
        bool RequireAuthenticatedByDefault,
        EquatableArray<ResourceBinding> Resources,
        bool HasFeature,
        bool HasValidation,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private sealed record Decorator(
        string Fqn,
        string OpenFqn,
        EquatableArray<Dependency> Dependencies,
        bool Conditional);

    private sealed record Dependency(string Fqn, bool IsMetadata);

    private sealed record ResourceBinding(
        string ResourceTypeFqn,
        string Operation,
        string IdPath,
        string? ResourceTypeName);
}
