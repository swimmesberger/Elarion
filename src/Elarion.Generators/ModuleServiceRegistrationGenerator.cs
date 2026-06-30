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

    private const string FeatureVariantAttributeMetadataName =
        "Elarion.Abstractions.Features.FeatureVariantAttribute`1";

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

    private sealed record ServiceInfo(
        string ServiceIdentifier,
        string Namespace,
        string ImplementationFqn,
        string AnchorFqn,
        string HintName,
        ServiceScope Scope,
        bool IsHostedService,
        EquatableArray<string> ContractsForRegistration)
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

    /// <summary>A discovered service: either a registration model or the diagnostics that rejected it.</summary>
    private sealed record ServiceResult(ServiceInfo? Service, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames
    {
        public const string Services = "Services";
        public const string Combined = "ServicesCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ServiceAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateServiceResult(ctx))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Services);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = services.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((results, modules), hasTrigger) = source;
            if (!hasTrigger)
            {
                return;
            }

            foreach (var result in results)
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            var serviceList = new List<ServiceInfo>();
            foreach (var result in results)
            {
                if (result.Service is not null)
                {
                    serviceList.Add(result.Service);
                }
            }

            foreach (var service in serviceList.OrderBy(s => s.HintName, StringComparer.Ordinal))
            {
                var code = GeneratePerServiceRegistration(service);
                spc.AddSource($"{service.HintName}.g.cs", SourceText.From(code, Encoding.UTF8));
            }

            GenerateModuleAggregations(spc, serviceList, modules);
        });
    }

    private static ServiceResult? CreateServiceResult(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract || ctx.Attributes.Length == 0)
        {
            return null;
        }

        var compilation = ctx.SemanticModel.Compilation;

        // A [FeatureVariant] modifies how its [Service] is registered (keyed + transparent contract registration,
        // emitted by VariantServiceRegistrationGenerator). Skip the plain registration here so the two never
        // double-register the same contract.
        if (HasFeatureVariantAttribute(classSymbol, compilation))
        {
            return null;
        }

        var serviceScopeSymbol = compilation.GetTypeByMetadataName(ServiceScopeMetadataName);
        if (serviceScopeSymbol is null)
        {
            return null;
        }

        var hostedServiceSymbol = compilation.GetTypeByMetadataName(HostedServiceMetadataName);
        var backgroundServiceSymbol = compilation.GetTypeByMetadataName(BackgroundServiceMetadataName);

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var serviceAttr = ctx.Attributes[0];
        var location = (ctx.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (IsGenericOrNestedInGenericType(classSymbol))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                GenericServicesAreNotSupported,
                location,
                classSymbol.ToDisplayString(fmt)));
            return new ServiceResult(null, diagnostics.ToImmutable());
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

            diagnostics.Add(DiagnosticInfo.Create(
                InvalidExplicitServiceContract,
                location,
                explicitContract.ToDisplayString(fmt),
                classSymbol.ToDisplayString(fmt)));
            hasInvalidExplicitContracts = true;
        }

        if (hasInvalidExplicitContracts)
        {
            return new ServiceResult(null, diagnostics.ToImmutable());
        }

        var resolvedContracts = ResolveContracts(classSymbol, explicitContracts, fmt);
        var isHostedService = IsHostedService(classSymbol, hostedServiceSymbol, backgroundServiceSymbol);
        if (isHostedService && scope != ServiceScope.Singleton)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                HostedServiceScopeMustBeSingleton,
                location,
                classSymbol.ToDisplayString(fmt),
                scope.ToString()));
            return new ServiceResult(null, diagnostics.ToImmutable());
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

        var service = new ServiceInfo(
            serviceIdentifier,
            ns,
            implementationFqn,
            anchor,
            hintName,
            scope,
            isHostedService,
            contractsForRegistration);
        return new ServiceResult(service, ImmutableArray<DiagnosticInfo>.Empty);
    }

    private static bool HasFeatureVariantAttribute(INamedTypeSymbol classSymbol, Compilation compilation)
    {
        var featureVariantSymbol = compilation.GetTypeByMetadataName(FeatureVariantAttributeMetadataName);
        if (featureVariantSymbol is null)
        {
            return false;
        }

        foreach (var attribute in classSymbol.GetAttributes())
        {
            // The attribute is the generic FeatureVariantAttribute<TContract>; compare the unbound definition.
            if (attribute.AttributeClass is { } attributeClass &&
                SymbolEqualityComparer.Default.Equals(attributeClass.OriginalDefinition, featureVariantSymbol))
            {
                return true;
            }
        }

        return false;
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
        IReadOnlyList<ModuleScanner.Module> modules)
    {
        var moduleServices = modules.ToDictionary(module => module, _ => new List<ServiceInfo>());
        foreach (var service in services)
        {
            ModuleScanner.Module? bestMatch = null;
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

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddServicesMethod,
                "Services",
                $"{nsPrefix}{moduleName}ServiceExtensions.Add{moduleName}Services(services);");
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

    // Delegate to the canonical matcher so this generator cannot drift from the shared namespace-scope rule.
    private static bool IsNamespaceInScope(string candidateNamespace, string scopeNamespace) =>
        ModuleScanner.IsInScope(candidateNamespace, scopeNamespace);

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
