using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates per-service and per-module DI registration extension methods for classes
/// annotated with <c>[Service]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ModuleServiceRegistrationGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateModuleServicesAttribute";

    private const string ServiceAttributeMetadataName =
        "Elarion.Abstractions.ServiceAttribute";

    private const string ServiceScopeMetadataName =
        "Elarion.Abstractions.ServiceScope";

    private const string AppModuleAttributeMetadataName =
        "Elarion.Abstractions.Modules.AppModuleAttribute";

    private const string HostedServiceMetadataName =
        "Microsoft.Extensions.Hosting.IHostedService";

    private const string BackgroundServiceMetadataName =
        "Microsoft.Extensions.Hosting.BackgroundService";

    private static readonly DiagnosticDescriptor HostedServiceScopeMustBeSingleton = new(
        id: "ELSG001",
        title: "Hosted service scope must be singleton",
        messageFormat:
        "Hosted service '{0}' must use ServiceScope.Singleton. Current scope is '{1}'.",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidExplicitServiceContract = new(
        id: "ELSG002",
        title: "Invalid explicit service contract",
        messageFormat:
        "Service contract '{0}' is not assignable from implementation '{1}'",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericServicesAreNotSupported = new(
        id: "ELSG003",
        title: "Generic services are not supported",
        messageFormat:
        "Generic service implementation '{0}' is not supported",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private sealed record ModuleInfo(string Name, string Namespace);

    private sealed record ServiceInfo(
        string ServiceIdentifier,
        string Namespace,
        string ImplementationFqn,
        string AnchorFqn,
        string HintName,
        ServiceScope Scope,
        bool IsHostedService,
        ImmutableArray<string> ContractsForRegistration)
    {
        public string RegistrationTypeName => $"{ServiceIdentifier}ServiceRegistration";

        public string RegistrationMethodName => $"Add{ServiceIdentifier}Service";
    }

    private enum ServiceScope
    {
        Scoped = 0,
        Singleton = 1,
        Transient = 2
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
        {
            var hasTrigger = FrameworkFeatureTriggers.HasAssemblyTrigger(compilation, TriggerAttributeMetadataName);
            if (!hasTrigger)
            {
                return;
            }

            var serviceAttributeSymbol = compilation.GetTypeByMetadataName(ServiceAttributeMetadataName);
            if (serviceAttributeSymbol is null)
            {
                return;
            }

            var serviceScopeSymbol = compilation.GetTypeByMetadataName(ServiceScopeMetadataName);
            if (serviceScopeSymbol is null)
            {
                return;
            }

            var hostedServiceSymbol = compilation.GetTypeByMetadataName(HostedServiceMetadataName);
            var backgroundServiceSymbol = compilation.GetTypeByMetadataName(BackgroundServiceMetadataName);
            var appModuleAttributeSymbol = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
            if (appModuleAttributeSymbol is null)
            {
                return;
            }

            var modules = CollectModules(compilation, appModuleAttributeSymbol);
            var services = CollectServices(
                compilation,
                serviceAttributeSymbol,
                serviceScopeSymbol,
                hostedServiceSymbol,
                backgroundServiceSymbol,
                spc);

            foreach (var service in services.OrderBy(s => s.HintName, StringComparer.Ordinal))
            {
                var code = GeneratePerServiceRegistration(service);
                spc.AddSource($"{service.HintName}.g.cs", SourceText.From(code, Encoding.UTF8));
            }

            GenerateModuleAggregations(spc, services, modules);
        });
    }

    private static List<ModuleInfo> CollectModules(
        Compilation compilation,
        INamedTypeSymbol appModuleAttributeSymbol)
    {
        var modules = new List<ModuleInfo>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol)
                {
                    continue;
                }

                if (!seenSymbols.Add(typeSymbol))
                {
                    continue;
                }

                var moduleAttr = typeSymbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, appModuleAttributeSymbol));
                if (moduleAttr is null)
                {
                    continue;
                }

                if (moduleAttr.ConstructorArguments.Length > 0 &&
                    moduleAttr.ConstructorArguments[0].Value is string moduleName)
                {
                    var ns = GetNamespace(typeSymbol);
                    modules.Add(new ModuleInfo(moduleName, ns));
                }
            }
        }

        return modules;
    }

    private static List<ServiceInfo> CollectServices(
        Compilation compilation,
        INamedTypeSymbol serviceAttributeSymbol,
        INamedTypeSymbol serviceScopeSymbol,
        INamedTypeSymbol? hostedServiceSymbol,
        INamedTypeSymbol? backgroundServiceSymbol,
        SourceProductionContext spc)
    {
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var infos = new List<ServiceInfo>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol ||
                    classSymbol.IsAbstract)
                {
                    continue;
                }

                var serviceAttr = classSymbol.GetAttributes()
                    .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, serviceAttributeSymbol));
                if (serviceAttr is null)
                {
                    continue;
                }

                if (!seenSymbols.Add(classSymbol))
                {
                    continue;
                }

                if (IsGenericOrNestedInGenericType(classSymbol))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        GenericServicesAreNotSupported,
                        classDecl.Identifier.GetLocation(),
                        classSymbol.ToDisplayString(fmt)));
                    continue;
                }

                var scope = ParseScope(serviceAttr, serviceScopeSymbol);
                var explicitContracts = GetExplicitContracts(serviceAttr).ToImmutableArray();
                var hasInvalidExplicitContracts = false;
                foreach (var explicitContract in explicitContracts)
                {
                    if (IsAssignableTo(classSymbol, explicitContract))
                    {
                        continue;
                    }

                    spc.ReportDiagnostic(Diagnostic.Create(
                        InvalidExplicitServiceContract,
                        classDecl.Identifier.GetLocation(),
                        explicitContract.ToDisplayString(fmt),
                        classSymbol.ToDisplayString(fmt)));
                    hasInvalidExplicitContracts = true;
                }

                if (hasInvalidExplicitContracts)
                {
                    continue;
                }

                var resolvedContracts = ResolveContracts(classSymbol, explicitContracts, fmt);
                var isHostedService = IsHostedService(classSymbol, hostedServiceSymbol, backgroundServiceSymbol);
                if (isHostedService && scope != ServiceScope.Singleton)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        HostedServiceScopeMustBeSingleton,
                        classDecl.Identifier.GetLocation(),
                        classSymbol.ToDisplayString(fmt),
                        scope));
                    continue;
                }

                var contractsForRegistration = RemoveHostedServiceContract(resolvedContracts);
                if (contractsForRegistration.IsEmpty)
                {
                    contractsForRegistration = ImmutableArray.Create(classSymbol.ToDisplayString(fmt));
                }

                var anchor = contractsForRegistration[0];
                var implementationFqn = classSymbol.ToDisplayString(fmt);
                var serviceIdentifier = GetServiceIdentifier(classSymbol);
                var ns = GetNamespace(classSymbol);
                var hintName = classSymbol.ToDisplayString(fmt)
                    .Replace("global::", string.Empty)
                    .Replace(".", "_")
                    .Replace("<", "_")
                    .Replace(">", "_")
                    .Replace(",", "_")
                    .Replace(" ", string.Empty);

                infos.Add(new ServiceInfo(
                    serviceIdentifier,
                    ns,
                    implementationFqn,
                    anchor,
                    hintName,
                    scope,
                    isHostedService,
                    contractsForRegistration));
            }
        }

        return infos;
    }

    private static ServiceScope ParseScope(AttributeData serviceAttr, INamedTypeSymbol serviceScopeSymbol)
    {
        foreach (var namedArgument in serviceAttr.NamedArguments)
        {
            if (!string.Equals(namedArgument.Key, "Scope", StringComparison.Ordinal))
            {
                continue;
            }

            if (namedArgument.Value.Value is int intValue &&
                Enum.IsDefined(typeof(ServiceScope), intValue))
            {
                return (ServiceScope)intValue;
            }

            if (namedArgument.Value.Type is INamedTypeSymbol enumType &&
                SymbolEqualityComparer.Default.Equals(enumType, serviceScopeSymbol) &&
                namedArgument.Value.Value is not null &&
                int.TryParse(namedArgument.Value.Value.ToString(), out var parsedInt) &&
                Enum.IsDefined(typeof(ServiceScope), parsedInt))
            {
                return (ServiceScope)parsedInt;
            }
        }

        return ServiceScope.Scoped;
    }

    private static IEnumerable<ITypeSymbol> GetExplicitContracts(AttributeData serviceAttr)
    {
        if (serviceAttr.ConstructorArguments.Length == 0)
        {
            return Enumerable.Empty<ITypeSymbol>();
        }

        var firstArg = serviceAttr.ConstructorArguments[0];
        if (firstArg.Kind != TypedConstantKind.Array)
        {
            return Enumerable.Empty<ITypeSymbol>();
        }

        return firstArg.Values
            .Where(v => v.Value is ITypeSymbol)
            .Select(v => (ITypeSymbol)v.Value!);
    }

    private static ImmutableArray<string> ResolveContracts(
        INamedTypeSymbol classSymbol,
        ImmutableArray<ITypeSymbol> explicitContracts,
        SymbolDisplayFormat fmt)
    {
        var contracts = new List<string>();
        if (!explicitContracts.IsEmpty)
        {
            contracts.AddRange(explicitContracts.Select(c => c.ToDisplayString(fmt)));
            return DistinctPreservingOrder(contracts);
        }

        contracts.AddRange(classSymbol.Interfaces.Select(i => i.ToDisplayString(fmt)));
        if (contracts.Count == 0)
        {
            contracts.Add(classSymbol.ToDisplayString(fmt));
        }

        return DistinctPreservingOrder(contracts);
    }

    private static ImmutableArray<string> DistinctPreservingOrder(IEnumerable<string> contracts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var contract in contracts)
        {
            if (seen.Add(contract))
            {
                result.Add(contract);
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<string> RemoveHostedServiceContract(ImmutableArray<string> contracts)
    {
        var filtered = contracts
            .Where(contract => !string.Equals(contract, "global::" + HostedServiceMetadataName, StringComparison.Ordinal))
            .ToImmutableArray();
        return filtered;
    }

    private static bool IsHostedService(
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol? hostedServiceSymbol,
        INamedTypeSymbol? backgroundServiceSymbol)
    {
        var implementsHosted = hostedServiceSymbol is not null &&
            classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, hostedServiceSymbol));
        var derivesBackground = backgroundServiceSymbol is not null && InheritsFrom(classSymbol, backgroundServiceSymbol);
        return implementsHosted || derivesBackground;
    }

    private static bool InheritsFrom(INamedTypeSymbol type, ITypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignableTo(INamedTypeSymbol source, ITypeSymbol destination)
    {
        if (SymbolEqualityComparer.Default.Equals(source, destination))
        {
            return true;
        }

        if (destination.TypeKind == TypeKind.Interface &&
            source.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, destination)))
        {
            return true;
        }

        return InheritsFrom(source, destination);
    }

    private static bool IsGenericOrNestedInGenericType(INamedTypeSymbol classSymbol)
    {
        for (INamedTypeSymbol? current = classSymbol; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetServiceIdentifier(INamedTypeSymbol classSymbol)
    {
        var names = new Stack<string>();
        for (INamedTypeSymbol? current = classSymbol; current is not null; current = current.ContainingType)
        {
            names.Push(current.Name);
        }

        return string.Join("_", names);
    }

    private static string GetNamespace(INamedTypeSymbol typeSymbol)
    {
        var ns = typeSymbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return string.Empty;
        }

        return ns.ToDisplayString();
    }

    private static void GenerateModuleAggregations(
        SourceProductionContext spc,
        IReadOnlyList<ServiceInfo> services,
        IReadOnlyList<ModuleInfo> modules)
    {
        var moduleServices = modules.ToDictionary(module => module, _ => new List<ServiceInfo>());
        foreach (var service in services)
        {
            ModuleInfo? bestMatch = null;
            foreach (var module in modules)
            {
                if (!IsNamespaceInScope(service.Namespace, module.Namespace) ||
                    (bestMatch is not null && module.Namespace.Length <= bestMatch.Namespace.Length))
                {
                    continue;
                }

                bestMatch = module;
            }

            if (bestMatch is not null)
            {
                moduleServices[bestMatch].Add(service);
            }
        }

        foreach (var kvp in moduleServices.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var moduleName = module.Name;
            var serviceList = kvp.Value;
            if (serviceList.Count == 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Source: Elarion.Generators.ModuleServiceRegistrationGenerator (module aggregation)");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            AppendNamespaceDeclaration(sb, module.Namespace);
            sb.AppendLine($"public static class {moduleName}ServiceExtensions");
            sb.AppendLine("{");
            sb.AppendLine($"    public static IServiceCollection Add{moduleName}Services(this IServiceCollection services)");
            sb.AppendLine("    {");
            foreach (var service in serviceList.OrderBy(s => s.ServiceIdentifier, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"        {GetRegistrationTypeReference(service)}.{service.RegistrationMethodName}(services);");
            }

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource($"{moduleName}ServiceExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    private static string GeneratePerServiceRegistration(ServiceInfo service)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ModuleServiceRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        AppendNamespaceDeclaration(sb, service.Namespace);
        sb.AppendLine($"public static class {service.RegistrationTypeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static IServiceCollection {service.RegistrationMethodName}(this IServiceCollection services)");
        sb.AppendLine("    {");

        var lifetime = service.Scope switch
        {
            ServiceScope.Singleton => "ServiceLifetime.Singleton",
            ServiceScope.Transient => "ServiceLifetime.Transient",
            _ => "ServiceLifetime.Scoped"
        };

        sb.AppendLine($"        services.Add(new ServiceDescriptor(");
        sb.AppendLine($"            typeof({service.AnchorFqn}),");
        sb.AppendLine($"            typeof({service.ImplementationFqn}),");
        sb.AppendLine($"            {lifetime}));");

        foreach (var contract in service.ContractsForRegistration)
        {
            if (string.Equals(contract, service.AnchorFqn, StringComparison.Ordinal))
            {
                continue;
            }

            sb.AppendLine("        services.Add(new ServiceDescriptor(");
            sb.AppendLine($"            typeof({contract}),");
            sb.AppendLine($"            sp => sp.GetRequiredService<{service.AnchorFqn}>(),");
            sb.AppendLine($"            {lifetime}));");
        }

        if (service.IsHostedService)
        {
            sb.AppendLine("        services.Add(new ServiceDescriptor(");
            sb.AppendLine("            typeof(global::Microsoft.Extensions.Hosting.IHostedService),");
            sb.AppendLine(
                $"            sp => (global::Microsoft.Extensions.Hosting.IHostedService)sp.GetRequiredService<{service.AnchorFqn}>(),");
            sb.AppendLine("            ServiceLifetime.Singleton));");
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static bool IsNamespaceInScope(string candidateNamespace, string scopeNamespace)
    {
        if (scopeNamespace.Length == 0)
        {
            return true;
        }

        if (candidateNamespace == scopeNamespace)
        {
            return true;
        }

        return candidateNamespace.StartsWith(scopeNamespace + ".", StringComparison.Ordinal);
    }

    private static string GetRegistrationTypeReference(ServiceInfo service)
    {
        if (service.Namespace.Length == 0)
        {
            return service.RegistrationTypeName;
        }

        return $"global::{service.Namespace}.{service.RegistrationTypeName}";
    }

    private static void AppendNamespaceDeclaration(StringBuilder sb, string ns)
    {
        if (ns.Length == 0)
        {
            return;
        }

        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
    }
}
