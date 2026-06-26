using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates per-module authorization-policy registration extension methods. Discovers every
/// <c>[AuthorizationPolicy("name")]</c> class, groups it under its owning <c>[AppModule]</c> by longest-prefix
/// namespace match, and emits <c>Add{Module}AuthorizationPolicies(this IServiceCollection)</c> registering each
/// policy by name — wired into the module's <c>ConfigureDefaultServices</c> like the other categories, so a policy
/// is auto-registered just by being declared (no manual <c>AddElarionAuthorizationPolicy</c> call).
/// <para>Trigger: <c>[assembly: UseElarion]</c> or <c>[assembly: GenerateModuleAuthorizationPolicies]</c>.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AuthorizationPolicyRegistrationGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Elarion.Abstractions.Authorization.AuthorizationPolicyAttribute";
    private const string PolicyInterfaceMetadataName = "Elarion.Abstractions.Authorization.IAuthorizationPolicy";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateModuleAuthorizationPoliciesAttribute";

    private static readonly DiagnosticDescriptor NotAnAuthorizationPolicy = new(
        "ELPOL001",
        "[AuthorizationPolicy] must be on an IAuthorizationPolicy",
        "Type '{0}' is annotated with [AuthorizationPolicy] but does not implement IAuthorizationPolicy; it will not be registered",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PolicyNotInModule = new(
        "ELPOL002",
        "Authorization policy is not in any module",
        "Authorization policy '{0}' is not under any [AppModule] namespace, so it is not auto-registered; move it under a module or register it manually",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record PolicyInfo(
        string PolicyFqn,
        string Name,
        string Namespace,
        bool ImplementsInterface,
        LocationInfo Location,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames
    {
        public const string Policies = "AuthorizationPolicies";
        public const string Combined = "AuthorizationPoliciesCombined";
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var policies = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreatePolicy(ctx))
            .Where(static policy => policy is not null)
            .Select(static (policy, _) => policy!)
            .Collect()
            .WithTrackingName(TrackingNames.Policies);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = policies.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((policyList, modules), hasTrigger) = source;
            if (!hasTrigger)
            {
                return;
            }

            Emit(spc, policyList, modules);
        });
    }

    private static PolicyInfo? CreatePolicy(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || type.IsAbstract)
        {
            return null;
        }

        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].ConstructorArguments.Length == 0 ||
            ctx.Attributes[0].ConstructorArguments[0].Value is not string name ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var policyInterface = ctx.SemanticModel.Compilation.GetTypeByMetadataName(PolicyInterfaceMetadataName);
        var implements = policyInterface is not null &&
            type.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface, policyInterface));

        var location = LocationInfo.From(type);
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        if (!implements)
        {
            diagnostics.Add(DiagnosticInfo.Create(NotAnAuthorizationPolicy, location, type.ToDisplayString(fmt)));
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
        return new PolicyInfo(type.ToDisplayString(fmt), name, ns, implements, location, diagnostics.ToImmutable());
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<PolicyInfo> policies,
        EquatableArray<ModuleScanner.Module> modules)
    {
        foreach (var policy in policies)
        {
            foreach (var diagnostic in policy.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
        }

        var modulePolicies = modules.ToDictionary(module => module, _ => new List<(string Fqn, string Name)>());

        foreach (var policy in policies)
        {
            if (!policy.ImplementsInterface)
            {
                continue;
            }

            ModuleScanner.Module? bestMatch = null;
            foreach (var module in modules)
                if (ModuleScanner.IsInScope(policy.Namespace, module.Namespace) &&
                    (bestMatch is null || module.Namespace.Length > bestMatch.Namespace.Length))
                    bestMatch = module;

            if (bestMatch is not null)
                modulePolicies[bestMatch].Add((policy.PolicyFqn, policy.Name));
            else
                spc.ReportDiagnostic(DiagnosticInfo.Create(PolicyNotInModule, policy.Location, policy.PolicyFqn).ToDiagnostic());
        }

        foreach (var kvp in modulePolicies
                     .Where(x => x.Value.Count > 0)
                     .OrderBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var moduleName = module.Name;

            var sb = new StringBuilder();
            sb.AppendLine("using Elarion.Authorization;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {module.Namespace};");
            sb.AppendLine();
            sb.AppendLine("/// <summary>");
            sb.AppendLine($"/// Extension methods for registering {moduleName} module authorization policies.");
            sb.AppendLine("/// </summary>");
            sb.AppendLine($"public static class {moduleName}AuthorizationPolicyExtensions");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// Registers all [AuthorizationPolicy] policies for the {moduleName} module.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public static IServiceCollection Add{moduleName}AuthorizationPolicies(");
            sb.AppendLine("        this IServiceCollection services)");
            sb.AppendLine("    {");

            foreach (var (fqn, name) in kvp.Value.OrderBy(x => x.Fqn, StringComparer.Ordinal))
                sb.AppendLine(
                    $"        services.AddElarionAuthorizationPolicy<{fqn}>({SymbolDisplay.FormatLiteral(name, quote: true)});");

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource(
                $"{moduleName}AuthorizationPolicyExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddAuthorizationPoliciesMethod,
                "AuthorizationPolicies",
                $"{nsPrefix}{moduleName}AuthorizationPolicyExtensions.Add{moduleName}AuthorizationPolicies(services);");
        }
    }
}
