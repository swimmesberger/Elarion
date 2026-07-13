using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the DI registration for variant services declared with
/// <c>[FeatureVariant("feature", Variant = "x")]</c> (selected per user by the feature flag's allocated
/// variant) or <c>[ConfigurationVariant("key", Value = "x")]</c> (selected process-globally by a configuration
/// value): keyed implementation registrations, the per-contract binding, the imperative
/// <c>IVariantServiceProvider&lt;T&gt;</c>, and the transparent unkeyed registration of the contract. For the
/// feature axis the transparent registration reads the warmed <c>VariantResolutionCache</c> and the
/// async-resolving handler proxy that warms it is emitted by <see cref="HandlerRegistrationGenerator"/>; the
/// configuration axis resolves synchronously, so its handlers keep the plain synchronous registration.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VariantServiceRegistrationGenerator : IIncrementalGenerator {
    private const string FeatureVariantAttributeMetadataName = "Elarion.Abstractions.Features.FeatureVariantAttribute";
    private const string ConfigurationVariantAttributeMetadataName = "Elarion.Abstractions.Features.ConfigurationVariantAttribute";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateModuleServicesAttribute";
    private const string DefaultKeySentinelExpr = "global::Elarion.Abstractions.Features.VariantServiceKeys.Default";

    private static readonly DiagnosticDescriptor DuplicateVariantKey = new(
        "ELVAR001", "Duplicate variant key",
        "Variant key '{0}' is declared more than once for contract '{1}'",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoDefaultVariant = new(
        "ELVAR003", "Variant contract has no default implementation",
        "Contract '{0}' has no default {1} implementation (one declared without a {2}); resolution when no "
        + "variant matches will fail",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingSelector = new(
        "ELVAR004", "Conflicting variant selector",
        "Contract '{0}' is bound to more than one selector ('{1}' and '{2}'); a contract maps to exactly one "
        + "feature or configuration key",
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
        "Variant service '{0}' must also be annotated with [Service]; {1} is a modifier on a service "
        + "registration (the [Service] declares the service and its lifetime)",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MixedSelectionAxes = new(
        "ELVAR008", "Variant contract mixes selection axes",
        "Contract '{0}' is bound by both [FeatureVariant] and [ConfigurationVariant]; a contract is selected by "
        + "exactly one axis",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BlankConfigurationKey = new(
        "ELVAR009", "Variant service declares a blank configuration key",
        "Variant service '{0}' declares a blank configuration key",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    // Value is the declared variant key ("office365", lower-cased for the configuration axis), or null for an
    // unnamed default (registered under the collision-proof sentinel key). IsDefault is true for an unnamed
    // default AND for a named default (Value + IsDefault = true), which stays selectable by its own value.
    private sealed record VariantImpl(
        string ContractFqn,
        string SelectorKey,
        bool IsConfiguration,
        string ImplFqn,
        string? Value,
        bool IsDefault,
        string Lifetime,
        string Namespace,
        LocationInfo Location);

    // One [FeatureVariant]/[ConfigurationVariant] class yields one VariantImpl per service contract (a
    // multi-interface [Service] is variant-resolved on each of its contracts), or diagnostics that rejected it.
    private sealed record VariantResult(EquatableArray<VariantImpl> Impls, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames {
        public const string Variants = "VariantServices";
        public const string ConfigurationVariants = "ConfigurationVariantServices";
        public const string Combined = "VariantServicesCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var featureVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateVariantResult(ctx, isConfiguration: false))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Variants);

        var configurationVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ConfigurationVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateVariantResult(ctx, isConfiguration: true))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.ConfigurationVariants);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = featureVariants.Combine(configurationVariants).Combine(modules).Combine(trigger)
            .WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var (((featureResults, configurationResults), modules), hasTrigger) = source;
            if (!hasTrigger) {
                return;
            }

            var results = featureResults.AddRange(configurationResults);
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

    private static VariantResult? CreateVariantResult(GeneratorAttributeSyntaxContext ctx, bool isConfiguration) {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract || ctx.Attributes.Length == 0) {
            return null;
        }

        var attribute = ctx.Attributes[0];
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var location = LocationInfo.From((ctx.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation());
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var implFqn = classSymbol.ToDisplayString(fmt);
        var attributeDisplay = isConfiguration ? "[ConfigurationVariant]" : "[FeatureVariant]";

        if (IsGenericOrNested(classSymbol)) {
            diagnostics.Add(DiagnosticInfo.Create(GenericImplementation, location, implFqn));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        var selectorKey = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(selectorKey)) {
            diagnostics.Add(DiagnosticInfo.Create(
                isConfiguration ? BlankConfigurationKey : BlankFeature, location, implFqn));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        // [FeatureVariant]/[ConfigurationVariant] is a modifier on a [Service]: the [Service] declares the
        // service, its contract(s), and its lifetime, and is required (ELVAR007). The contracts are exactly what
        // [Service] registers under (shared resolver), so the variant is applied to each of them — nothing to
        // repeat on the variant attribute. The service generator skips the plain registration for a variant
        // class, so this generator owns the keyed + transparent registration.
        var serviceAttr = ServiceContractResolver.FindServiceAttribute(classSymbol);
        if (serviceAttr is null) {
            diagnostics.Add(DiagnosticInfo.Create(VariantRequiresService, location, implFqn, attributeDisplay));
            return new VariantResult(EquatableArray<VariantImpl>.Empty, diagnostics.ToImmutable());
        }

        var variantPropertyName = isConfiguration ? "Value" : "Variant";
        string? variant = null;
        var isDefaultFlag = false;
        foreach (var named in attribute.NamedArguments) {
            if (named.Key == variantPropertyName && named.Value.Value is string v && !string.IsNullOrWhiteSpace(v)) {
                variant = v;
            }

            if (named.Key == "IsDefault" && named.Value.Value is bool d) {
                isDefaultFlag = d;
            }
        }

        // Configuration variants match the configured value case-insensitively: the DI key is lower-cased here
        // and the runtime lowers the configured value before the keyed lookup.
        if (isConfiguration && variant is not null) {
            variant = variant.ToLowerInvariant();
        }

        var lifetime = ParseServiceLifetime(serviceAttr);
        var ns = GetNamespace(classSymbol);

        var contracts = ServiceContractResolver.ResolveContractFqns(classSymbol, serviceAttr, fmt);
        var builder = ImmutableArray.CreateBuilder<VariantImpl>(contracts.Length);
        foreach (var contractFqn in contracts) {
            builder.Add(new VariantImpl(
                ContractFqn: contractFqn,
                SelectorKey: selectorKey,
                IsConfiguration: isConfiguration,
                ImplFqn: implFqn,
                Value: variant,
                IsDefault: variant is null || isDefaultFlag,
                Lifetime: lifetime,
                Namespace: ns,
                Location: location));
        }

        return new VariantResult(builder.ToImmutable(), ImmutableArray<DiagnosticInfo>.Empty);
    }

    private sealed record ContractGroup(
        string ContractFqn, string SelectorKey, bool IsConfiguration, string Namespace, string HintName,
        bool HasDefault, string DefaultKeyExpr, IReadOnlyList<VariantImpl> Impls) {
        public string Identifier => Sanitize(ContractFqn);

        public string RegistrationTypeName => $"{Identifier}VariantServiceRegistration";

        public string RegistrationMethodName => $"Add{Identifier}VariantService";
    }

    private static List<ContractGroup> BuildContractGroups(SourceProductionContext spc, List<VariantImpl> impls) {
        var groups = new List<ContractGroup>();
        foreach (var byContract in impls.GroupBy(static i => i.ContractFqn, StringComparer.Ordinal)) {
            var ordered = byContract.OrderBy(static i => i.ImplFqn, StringComparer.Ordinal).ToList();

            // ELVAR008: a contract is selected by exactly one axis — a per-user feature allocation or a global
            // configuration value, never both (the two disagree on wrapping the consuming handler).
            if (ordered.Any(static i => i.IsConfiguration) && ordered.Any(static i => !i.IsConfiguration)) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    MixedSelectionAxes, ordered[0].Location, ordered[0].ContractFqn).ToDiagnostic());
                continue;
            }

            var selectorKey = ordered[0].SelectorKey;
            var isConfiguration = ordered[0].IsConfiguration;

            // An error diagnostic for a contract gates its emission: shipping an arbitrary "first-alphabetical
            // wins" registration for a build the compiler already failed would ship silently-wrong behaviour.
            var hasError = false;

            // ELVAR004: a contract maps to exactly one feature / configuration key.
            foreach (var impl in ordered) {
                if (!string.Equals(impl.SelectorKey, selectorKey, StringComparison.Ordinal)) {
                    spc.ReportDiagnostic(DiagnosticInfo.Create(
                        ConflictingSelector, impl.Location, impl.ContractFqn, selectorKey, impl.SelectorKey).ToDiagnostic());
                    hasError = true;
                }
            }

            // ELVAR001 (a): more than one default — unnamed or IsDefault = true — for one contract.
            var defaults = ordered.Where(static i => i.IsDefault).ToList();
            for (var extra = 1; extra < defaults.Count; extra++) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    DuplicateVariantKey, defaults[extra].Location, "(default)", defaults[extra].ContractFqn).ToDiagnostic());
                hasError = true;
            }

            // ELVAR001 (b): duplicate named variant key within the contract (configuration values are
            // pre-lower-cased, so Ordinal also catches case-only duplicates there).
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var impl in ordered) {
                if (impl.Value is not null && !seenKeys.Add(impl.Value)) {
                    spc.ReportDiagnostic(DiagnosticInfo.Create(
                        DuplicateVariantKey, impl.Location, impl.Value, impl.ContractFqn).ToDiagnostic());
                    hasError = true;
                }
            }

            if (hasError) {
                // Suppress emission for this contract; the reported error is the actionable output.
                continue;
            }

            var defaultImpl = defaults.Count > 0 ? defaults[0] : null;
            if (defaultImpl is null) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    NoDefaultVariant, ordered[0].Location, ordered[0].ContractFqn,
                    isConfiguration ? "[ConfigurationVariant]" : "[FeatureVariant]",
                    isConfiguration ? "Value" : "Variant").ToDiagnostic());
            }

            // A named default (Value + IsDefault) is registered under its own value key and becomes the
            // binding's default key, so the default state stays explicitly selectable/writable by name.
            var defaultKeyExpr = defaultImpl is null
                ? "null"
                : defaultImpl.Value is null ? DefaultKeySentinelExpr : FormatLiteral(defaultImpl.Value);

            groups.Add(new ContractGroup(
                ContractFqn: ordered[0].ContractFqn,
                SelectorKey: selectorKey,
                IsConfiguration: isConfiguration,
                Namespace: ordered[0].Namespace,
                HintName: HintNames.Sanitize(ordered[0].ContractFqn) + "_VariantService",
                HasDefault: defaultImpl is not null,
                DefaultKeyExpr: defaultKeyExpr,
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

        var registrationMethod = group.IsConfiguration
            ? "AddElarionConfigurationVariantService"
            : "AddElarionVariantService";
        sb.AppendLine("        global::Elarion.Abstractions.Features.VariantServiceCollectionExtensions");
        sb.AppendLine($"            .{registrationMethod}<{group.ContractFqn}>(services, {FormatLiteral(group.SelectorKey)}, {group.DefaultKeyExpr});");

        foreach (var impl in group.Impls) {
            var keyExpr = impl.Value is null ? DefaultKeySentinelExpr : FormatLiteral(impl.Value);
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
