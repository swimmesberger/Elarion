using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates a typed, in-process API per <c>[GenerateModuleApi]</c> partial interface: one method per
/// owning-module handler, dispatched typed-direct to <c>IHandler&lt;TRequest, TResponse&gt;</c> with no
/// serialization, plus an internal forwarder and a DI registration wired into the module's gated
/// <c>ConfigureDefaultServices</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ModuleApiGenerator : IIncrementalGenerator
{
    private const string GenerateModuleApiAttributeMetadataName =
        "Elarion.Abstractions.Modules.GenerateModuleApiAttribute";

    private const string ModuleApiAttributeMetadataName =
        "Elarion.Abstractions.Modules.ModuleApiAttribute";

    private const string HandlerInterfaceDisplay =
        "Elarion.Abstractions.IHandler<TRequest, TResponse>";

    private static readonly DiagnosticDescriptor FacadeNotPartial = new(
        "ELAPI001",
        "Module API interface must be partial",
        "Interface '{0}' is annotated with [GenerateModuleApi] but is not declared partial; the generated members cannot be emitted",
        "Elarion.Modules",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor FacadeNested = new(
        "ELAPI002",
        "Module API interface must be top-level",
        "Interface '{0}' is annotated with [GenerateModuleApi] but is nested; declare it at namespace scope",
        "Elarion.Modules",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor FacadeNotInModule = new(
        "ELAPI003",
        "Module API interface is not in any module",
        "Interface '{0}' is annotated with [GenerateModuleApi] but its namespace is not under any [AppModule]; no handlers can be associated and it will be left empty",
        "Elarion.Modules",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateMethod = new(
        "ELAPI004",
        "Duplicate module API method",
        "Module API interface '{0}' maps more than one handler to the method name '{1}'; the duplicate handler is skipped",
        "Elarion.Modules",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private sealed record HandlerEntry(
        string Namespace,
        string Name,
        string RequestFqn,
        string ResponseFqn,
        bool Exclude,
        EquatableArray<string> Scopes);

    private sealed record FacadeEntry(
        string Namespace,
        string Name,
        string Accessibility,
        EquatableArray<string> Scopes,
        LocationInfo Location);

    /// <summary>A discovered facade: either a model or the diagnostics (ELAPI001/002) that rejected it.</summary>
    private sealed record FacadeResult(FacadeEntry? Facade, EquatableArray<DiagnosticInfo> Diagnostics);

    private sealed record MemberEntry(string MethodName, string RequestFqn, string ResponseFqn);

    private static class TrackingNames
    {
        public const string Facades = "Facades";
        public const string Handlers = "Handlers";
        public const string Combined = "ModuleApiCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Facades are attributed interfaces; handlers are detected structurally (IHandler<,>) and are not
        // attributed, so they need a separate predicate-filtered syntax provider. There is no assembly
        // trigger — the generator runs whenever a [GenerateModuleApi] facade exists.
        var facades = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateModuleApiAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (ctx, ct) => CreateFacadeResult(ctx, ct))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Facades);

        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => CreateHandler(ctx, ct))
            .Where(static handler => handler is not null)
            .Select(static (handler, _) => handler!)
            .Collect()
            .WithTrackingName(TrackingNames.Handlers);

        var modules = ModuleProviders.CollectModules(context);

        var combined = facades.Combine(handlers).Combine(modules).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((facadeResults, handlerList), modules) = source;
            Execute(spc, facadeResults, handlerList, modules);
        });
    }

    private static void Execute(
        SourceProductionContext spc,
        ImmutableArray<FacadeResult> facadeResults,
        ImmutableArray<HandlerEntry> handlerList,
        EquatableArray<ModuleScanner.Module> modules)
    {
        foreach (var result in facadeResults)
        {
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        }

        var facades = new List<FacadeEntry>();
        foreach (var result in facadeResults)
        {
            if (result.Facade is not null)
                facades.Add(result.Facade);
        }

        if (facades.Count == 0)
            return;

        // A partial handler class declared across files yields one transform result per declaration; the
        // entries are identical (value records), so Distinct collapses them to a single handler.
        var handlers = handlerList.Distinct().ToList();

        // Facades grouped by owning module, so each module's registration method covers all its facades.
        var registrationsByModule = new Dictionary<ModuleScanner.Module, List<(string InterfaceFqn, string ForwarderFqn)>>();

        foreach (var facade in facades.OrderBy(f => f.Namespace, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var module = ModuleScanner.FindBest(facade.Namespace, modules);
            if (module is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(FacadeNotInModule, facade.Location.ToLocation(), facade.Name));
                continue;
            }

            var members = SelectMembers(facade, module, handlers, modules, spc);

            var ns = facade.Namespace;
            var forwarderName = facade.Name + "Forwarder";
            var source = GenerateFacade(ns, facade, forwarderName, members);
            spc.AddSource(HintName(ns, facade.Name), SourceText.From(source, Encoding.UTF8));

            var nsPrefix = ns.Length > 0 ? $"global::{ns}." : "global::";
            if (!registrationsByModule.TryGetValue(module, out var list))
            {
                list = [];
                registrationsByModule[module] = list;
            }

            list.Add(($"{nsPrefix}{facade.Name}", $"{nsPrefix}{forwarderName}"));
        }

        foreach (var kvp in registrationsByModule.OrderBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var className = $"{module.Name}ModuleApiExtensions";
            var methodName = $"Add{module.Name}ModuleApi";
            var ns = module.Namespace.Length > 0 ? module.Namespace : null;

            var registration = GenerateRegistration(ns, className, methodName, kvp.Value);
            spc.AddSource($"{className}.g.cs", SourceText.From(registration, Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddModuleApiMethod,
                "ModuleApi",
                $"{nsPrefix}{className}.{methodName}(services);");
        }
    }

    private static List<MemberEntry> SelectMembers(
        FacadeEntry facade,
        ModuleScanner.Module module,
        IReadOnlyList<HandlerEntry> handlers,
        IReadOnlyList<ModuleScanner.Module> modules,
        SourceProductionContext spc)
    {
        var members = new List<MemberEntry>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handler in handlers
                     .OrderBy(h => h.Namespace, StringComparer.Ordinal)
                     .ThenBy(h => h.Name, StringComparer.Ordinal))
        {
            if (handler.Exclude)
                continue;

            var handlerModule = ModuleScanner.FindBest(handler.Namespace, modules);
            if (handlerModule is null || !handlerModule.Equals(module))
                continue;

            // No scopes on the facade = the module's default facade (every non-excluded handler).
            // Scoped facade = handlers whose own scope tags intersect the facade's scopes.
            if (!facade.Scopes.IsEmpty &&
                !facade.Scopes.Any(scope => handler.Scopes.Contains(scope, StringComparer.Ordinal)))
            {
                continue;
            }

            if (!usedNames.Add(handler.Name))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateMethod, facade.Location.ToLocation(), facade.Name, handler.Name));
                continue;
            }

            members.Add(new MemberEntry(handler.Name, handler.RequestFqn, handler.ResponseFqn));
        }

        return members;
    }

    private static FacadeResult? CreateFacadeResult(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || ctx.Attributes.Length == 0)
            return null;

        var attribute = ctx.Attributes[0];
        var location = LocationInfo.From(symbol);

        if (symbol.ContainingType is not null)
            return new FacadeResult(null, ImmutableArray.Create(DiagnosticInfo.Create(FacadeNested, location, symbol.Name)));

        var isPartial = symbol.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax(ct) is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
        if (!isPartial)
            return new FacadeResult(null, ImmutableArray.Create(DiagnosticInfo.Create(FacadeNotPartial, location, symbol.Name)));

        var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var accessibility = symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        var facade = new FacadeEntry(ns, symbol.Name, accessibility, GetScopes(attribute), location);
        return new FacadeResult(facade, EquatableArray<DiagnosticInfo>.Empty);
    }

    private static HandlerEntry? CreateHandler(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl ||
            ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol symbol ||
            symbol.IsAbstract)
        {
            return null;
        }

        var handlerInterface = symbol.AllInterfaces.FirstOrDefault(iface =>
            iface.OriginalDefinition.ToDisplayString() == HandlerInterfaceDisplay);
        if (handlerInterface is null)
            return null;

        if (symbol.ContainingNamespace?.ToDisplayString().Contains("Decorators") == true)
            return null;

        var exclude = false;
        var scopes = ImmutableArray<string>.Empty;
        var moduleApiAttr = ctx.SemanticModel.Compilation.GetTypeByMetadataName(ModuleApiAttributeMetadataName);
        if (moduleApiAttr is not null)
        {
            var apiAttr = symbol.GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleApiAttr));
            if (apiAttr is not null)
            {
                scopes = GetScopes(apiAttr);
                foreach (var named in apiAttr.NamedArguments)
                {
                    if (named.Key == "Exclude" && named.Value.Value is bool value)
                        exclude = value;
                }
            }
        }

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        return new HandlerEntry(
            symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            symbol.Name,
            handlerInterface.TypeArguments[0].ToDisplayString(fmt),
            handlerInterface.TypeArguments[1].ToDisplayString(fmt),
            exclude,
            scopes);
    }

    private static ImmutableArray<string> GetScopes(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var value in attribute.ConstructorArguments[0].Values)
        {
            if (value.Value is string scope && scope.Length > 0)
                builder.Add(scope);
        }

        return builder.ToImmutable();
    }

    private static string GenerateFacade(string ns, FacadeEntry facade, string forwarderName, List<MemberEntry> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ModuleApiGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (ns.Length > 0)
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>Generated typed in-process API over the module's handlers.</summary>");
        sb.AppendLine($"{facade.Accessibility} partial interface {facade.Name}");
        sb.AppendLine("{");
        foreach (var member in members)
            sb.AppendLine($"    {Signature(member)};");
        sb.AppendLine("}");
        sb.AppendLine();

        if (members.Count == 0)
        {
            // No handlers matched: a parameterless forwarder still satisfies the (empty) interface.
            sb.AppendLine($"internal sealed class {forwarderName} : {facade.Name}");
            sb.AppendLine("{");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // Handlers are resolved lazily per call from the service provider rather than injected into the
        // forwarder's constructor: a default facade spans every handler in the module, and eager constructor
        // injection would build every handler's decorator chain (and its dependencies) just to call one method.
        sb.AppendLine($"internal sealed class {forwarderName}(global::System.IServiceProvider services) : {facade.Name}");
        sb.AppendLine("{");
        foreach (var member in members)
        {
            sb.AppendLine($"    public {Signature(member)}");
            sb.AppendLine($"        => global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Elarion.Abstractions.IHandler<{member.RequestFqn}, {member.ResponseFqn}>>(services).HandleAsync(request, ct);");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Signature(MemberEntry member) =>
        $"global::System.Threading.Tasks.ValueTask<{member.ResponseFqn}> {member.MethodName}({member.RequestFqn} request, global::System.Threading.CancellationToken ct = default)";

    private static string GenerateRegistration(
        string? ns,
        string className,
        string methodName,
        List<(string InterfaceFqn, string ForwarderFqn)> facades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ModuleApiGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {methodName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        foreach (var (interfaceFqn, forwarderFqn) in facades)
            sb.AppendLine($"        services.TryAddScoped<{interfaceFqn}, {forwarderFqn}>();");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string HintName(string ns, string interfaceName)
    {
        var baseName = ns.Length > 0 ? $"{ns}.{interfaceName}" : interfaceName;
        var sb = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return $"{sb}.ModuleApi.g.cs";
    }
}
