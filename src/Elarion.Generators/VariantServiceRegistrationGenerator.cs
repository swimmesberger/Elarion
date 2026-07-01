using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the DI registration for variant services declared with
/// <c>[FeatureVariant&lt;TContract&gt;("feature", Variant = "x")]</c>: keyed implementation registrations, the
/// per-contract <c>VariantServiceBinding</c>, the imperative <c>IVariantServiceProvider&lt;T&gt;</c>, and the
/// transparent unkeyed registration of the contract (which reads the warmed <c>VariantResolutionCache</c>). The
/// async-resolving handler proxy that warms the cache is emitted by <see cref="HandlerRegistrationGenerator"/>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VariantServiceRegistrationGenerator : IIncrementalGenerator {
    private const string VariantServiceAttributeMetadataName = "Elarion.Abstractions.Features.FeatureVariantAttribute";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateModuleServicesAttribute";

    private static readonly DiagnosticDescriptor DuplicateVariantKey = new(
        "ELVAR001", "Duplicate variant key",
        "Variant key '{0}' is declared more than once for contract '{1}'",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoDefaultVariant = new(
        "ELVAR003", "Variant contract has no default implementation",
        "Contract '{0}' has no default [FeatureVariant] implementation (one declared without a Variant); resolution "
        + "for an unallocated user will fail",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingFeature = new(
        "ELVAR004", "Conflicting variant feature",
        "Contract '{0}' is bound to more than one feature ('{1}' and '{2}'); a contract maps to exactly one feature",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BlankFeature = new(
        "ELVAR005", "Variant service declares a blank feature",
        "Variant service '{0}' declares a blank feature name",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericImplementation = new(
        "ELVAR006", "Generic variant implementation is not supported",
        "Generic variant service implementation '{0}' is not supported",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor VariantRequiresService = new(
        "ELVAR007", "Variant implementation must also be a [Service]",
        "Variant service '{0}' must also be annotated with [Service]; [FeatureVariant] is a modifier on a service "
        + "registration (the [Service] declares the service and its lifetime)",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private sealed record VariantImpl(
        string ContractFqn,
        string Feature,
        string ImplFqn,
        string VariantKey,
        bool IsDefault,
        string Lifetime,
        string Namespace,
        LocationInfo Location);

    // One [FeatureVariant] class yields one VariantImpl per service contract (a multi-interface [Service] is
    // variant-resolved on each of its contracts), or diagnostics that rejected it.
    private sealed record VariantResult(EquatableArray<VariantImpl> Impls, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames {
        public const string Variants = "VariantServices";
        public const string Combined = "VariantServicesCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var variants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantServiceAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateVariantResult(ctx))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Variants);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = variants.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var ((results, modules), hasTrigger) = source;
            if (!hasTrigger) {
                return;
            }

            foreach (var result in results) {
                foreach (var diagnostic in result.Diagnostics) {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            var impls = results
                .SelectMany(static r => r.Impls.AsImmutableArray)
                .ToList();

            var groups = BuildContractGroups(spc, impls);

            foreach (var group in groups.OrderBy(static g => g.HintName, StringComparer.Ordinal)) {
                var code = GeneratePerContractRegistration(group);
                spc.AddSource($"{group.HintName}.g.cs", SourceText.From(code, Encoding.UTF8));
            }

            GenerateModuleAggregations(spc, groups, modules);
        });
    }

    private static VariantResult? CreateVariantResult(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract || ctx.Attributes.Length == 0) {
            return null;
        }

        var attribute = ctx.Attributes[0];
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var location = LocationInfo.From((ctx.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation());
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var implFqn = classSymbol.ToDisplayString(fmt);

        if (IsGenericOrNested(classSymbol)) {
            diagnostics.Add(DiagnosticInfo.Create(GenericImplementation, location, implFqn));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        var feature = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(feature)) {
            diagnostics.Add(DiagnosticInfo.Create(BlankFeature, location, implFqn));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        // [FeatureVariant] is a modifier on a [Service]: the [Service] declares the service, its contract(s), and
        // its lifetime, and is required (ELVAR007). The contracts are exactly what [Service] registers under (shared
        // resolver), so the variant is applied to each of them — nothing to repeat on [FeatureVariant]. The service
        // generator skips the plain registration for a [FeatureVariant] class, so this generator owns the keyed +
        // transparent registration.
        var serviceAttr = ServiceContractResolver.FindServiceAttribute(classSymbol);
        if (serviceAttr is null) {
            diagnostics.Add(DiagnosticInfo.Create(VariantRequiresService, location, implFqn));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        string? variant = null;
        foreach (var named in attribute.NamedArguments) {
            if (named.Key == "Variant" && named.Value.Value is string v && !string.IsNullOrWhiteSpace(v)) {
                variant = v;
            }
        }

        var lifetime = ParseServiceLifetime(serviceAttr);
        var variantKey = variant ?? "global::Elarion.Abstractions.Features.VariantServiceKeys.Default";
        var ns = GetNamespace(classSymbol);

        var contracts = ServiceContractResolver.ResolveContractFqns(classSymbol, serviceAttr, fmt);
        var builder = ImmutableArray.CreateBuilder<VariantImpl>(contracts.Length);
        foreach (var contractFqn in contracts) {
            builder.Add(new VariantImpl(
                ContractFqn: contractFqn,
                Feature: feature,
                ImplFqn: implFqn,
                VariantKey: variantKey,
                IsDefault: variant is null,
                Lifetime: lifetime,
                Namespace: ns,
                Location: location));
        }

        return new VariantResult(builder.ToImmutable(), ImmutableArray<DiagnosticInfo>.Empty);
    }

    private sealed record ContractGroup(
        string ContractFqn, string Feature, string Namespace, string HintName, bool HasDefault,
        IReadOnlyList<VariantImpl> Impls) {
        public string Identifier => Sanitize(ContractFqn);

        public string RegistrationTypeName => $"{Identifier}VariantServiceRegistration";

        public string RegistrationMethodName => $"Add{Identifier}VariantService";
    }

    private static List<ContractGroup> BuildContractGroups(SourceProductionContext spc, List<VariantImpl> impls) {
        var groups = new List<ContractGroup>();
        foreach (var byContract in impls.GroupBy(static i => i.ContractFqn, StringComparer.Ordinal)) {
            var ordered = byContract.OrderBy(static i => i.ImplFqn, StringComparer.Ordinal).ToList();
            var feature = ordered[0].Feature;

            // An error diagnostic for a contract gates its emission: shipping an arbitrary "first-alphabetical
            // wins" registration for a build the compiler already failed would ship silently-wrong behaviour.
            var hasError = false;

            // ELVAR004: a contract maps to exactly one feature.
            foreach (var impl in ordered) {
                if (!string.Equals(impl.Feature, feature, StringComparison.Ordinal)) {
                    spc.ReportDiagnostic(DiagnosticInfo.Create(
                        ConflictingFeature, impl.Location, impl.ContractFqn, feature, impl.Feature).ToDiagnostic());
                    hasError = true;
                }
            }

            // ELVAR001: duplicate variant key within the contract.
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var impl in ordered) {
                if (!seenKeys.Add(impl.VariantKey)) {
                    spc.ReportDiagnostic(DiagnosticInfo.Create(
                        DuplicateVariantKey, impl.Location,
                        impl.IsDefault ? "(default)" : impl.VariantKey, impl.ContractFqn).ToDiagnostic());
                    hasError = true;
                }
            }

            if (hasError) {
                // Suppress emission for this contract; the reported error is the actionable output.
                continue;
            }

            var hasDefault = ordered.Any(static i => i.IsDefault);
            if (!hasDefault) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    NoDefaultVariant, ordered[0].Location, ordered[0].ContractFqn).ToDiagnostic());
            }

            groups.Add(new ContractGroup(
                ContractFqn: ordered[0].ContractFqn,
                Feature: feature,
                Namespace: ordered[0].Namespace,
                HintName: Sanitize(ordered[0].ContractFqn) + "_VariantService",
                HasDefault: hasDefault,
                Impls: ordered));
        }

        return groups;
    }

    private static string GeneratePerContractRegistration(ContractGroup group) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.VariantServiceRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        AppendNamespaceDeclaration(sb, group.Namespace);
        sb.AppendLine($"public static class {group.RegistrationTypeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static IServiceCollection {group.RegistrationMethodName}(this IServiceCollection services)");
        sb.AppendLine("    {");

        var defaultKey = group.HasDefault
            ? "global::Elarion.Abstractions.Features.VariantServiceKeys.Default"
            : "null";
        sb.AppendLine("        global::Elarion.Abstractions.Features.VariantServiceCollectionExtensions");
        sb.AppendLine($"            .AddElarionVariantService<{group.ContractFqn}>(services, {FormatLiteral(group.Feature)}, {defaultKey});");

        foreach (var impl in group.Impls) {
            var keyExpr = impl.IsDefault ? impl.VariantKey : FormatLiteral(impl.VariantKey);
            sb.AppendLine("        services.Add(new ServiceDescriptor(");
            sb.AppendLine($"            typeof({impl.ContractFqn}),");
            sb.AppendLine($"            {keyExpr},");
            sb.AppendLine($"            typeof({impl.ImplFqn}),");
            sb.AppendLine($"            {impl.Lifetime}));");
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void GenerateModuleAggregations(
        SourceProductionContext spc,
        IReadOnlyList<ContractGroup> groups,
        IReadOnlyList<ModuleScanner.Module> modules) {
        var moduleGroups = modules.ToDictionary(module => module, _ => new List<ContractGroup>());
        foreach (var group in groups) {
            ModuleScanner.Module? best = null;
            foreach (var module in modules) {
                if (!ModuleScanner.IsInScope(group.Namespace, module.Namespace) ||
                    (best is not null && module.Namespace.Length <= best.Namespace.Length)) {
                    continue;
                }

                best = module;
            }

            if (best is not null) {
                moduleGroups[best].Add(group);
            }
        }

        foreach (var kvp in moduleGroups.OrderBy(k => k.Key.Name, StringComparer.Ordinal)) {
            var module = kvp.Key;
            var groupList = kvp.Value;
            if (groupList.Count == 0) {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Source: Elarion.Generators.VariantServiceRegistrationGenerator (module aggregation)");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            AppendNamespaceDeclaration(sb, module.Namespace);
            sb.AppendLine($"public static class {module.Name}VariantServiceExtensions");
            sb.AppendLine("{");
            sb.AppendLine($"    public static IServiceCollection Add{module.Name}VariantServices(this IServiceCollection services)");
            sb.AppendLine("    {");
            foreach (var group in groupList.OrderBy(g => g.HintName, StringComparer.Ordinal)) {
                sb.AppendLine($"        {GetTypeReference(group)}.{group.RegistrationMethodName}(services);");
            }

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource($"{module.Name}VariantServiceExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddVariantServicesMethod,
                "VariantServices",
                $"{nsPrefix}{module.Name}VariantServiceExtensions.Add{module.Name}VariantServices(services);");
        }
    }

    private static string GetTypeReference(ContractGroup group) =>
        group.Namespace.Length == 0
            ? group.RegistrationTypeName
            : $"global::{group.Namespace}.{group.RegistrationTypeName}";

    // The variant implementation's DI lifetime comes from its [Service] (Scope), mapping ServiceScope
    // (Scoped=0/Singleton=1/Transient=2) to the ServiceLifetime used by the emitted ServiceDescriptor.
    private static string ParseServiceLifetime(AttributeData serviceAttr) {
        foreach (var named in serviceAttr.NamedArguments) {
            if (named.Key == "Scope" && named.Value.Value is int s) {
                return s switch {
                    1 => "ServiceLifetime.Singleton",
                    2 => "ServiceLifetime.Transient",
                    _ => "ServiceLifetime.Scoped"
                };
            }
        }

        return "ServiceLifetime.Scoped";
    }

    private static bool IsGenericOrNested(INamedTypeSymbol classSymbol) {
        for (INamedTypeSymbol? current = classSymbol; current is not null; current = current.ContainingType) {
            if (current.TypeParameters.Length > 0) {
                return true;
            }
        }

        return false;
    }

    private static string GetNamespace(INamedTypeSymbol typeSymbol) =>
        typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;

    private static void AppendNamespaceDeclaration(StringBuilder sb, string ns) {
        if (ns.Length == 0) {
            return;
        }

        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
    }

    private static string FormatLiteral(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Sanitize(string value) {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Replace("global::", string.Empty)) {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return sb.ToString();
    }
}
