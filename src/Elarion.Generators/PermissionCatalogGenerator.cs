using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the per-module permission-catalog registration that populates <c>IPermissionCatalog</c>. Discovers
/// every <c>[RequirePermission("…")]</c> and <c>[RequireRole("…")]</c> on a handler, groups the strings under the
/// handler's owning <c>[AppModule]</c> by longest-prefix namespace match, and emits
/// <c>Add{Module}Permissions(this IServiceCollection)</c> registering one <c>PermissionCatalogModule</c> — wired
/// into the module's <c>ConfigureDefaultServices</c> like the other categories, so a guarded handler contributes
/// its permission to the catalog automatically (no central <c>Permissions.All</c> list to maintain). The core
/// <c>IPermissionCatalog</c> aggregates every registered module across assemblies.
/// <para>Trigger: <c>[assembly: UseElarion]</c> or <c>[assembly: GeneratePermissionCatalog]</c>.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PermissionCatalogGenerator : IIncrementalGenerator
{
    private const string RequirePermissionAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequirePermissionAttribute";
    private const string RequireRoleAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequireRoleAttribute";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GeneratePermissionCatalogAttribute";
    private const string PermissionCatalogModuleFqn = "global::Elarion.Abstractions.Authorization.PermissionCatalogModule";

    private static readonly DiagnosticDescriptor RequirementNotInModule = new(
        "ELPERM001",
        "Authorization requirement is not in any module",
        "Handler '{0}' declares authorization requirements but is not under any [AppModule] namespace, so they "
        + "are not added to the permission catalog; move it under a module",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record GuardSet(
        string HandlerFqn,
        string Namespace,
        EquatableArray<string> Values,
        LocationInfo Location);

    private static class TrackingNames
    {
        public const string Permissions = "PermissionCatalogPermissions";
        public const string Roles = "PermissionCatalogRoles";
        public const string Combined = "PermissionCatalogCombined";
    }

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var permissions = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RequirePermissionAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateGuardSet(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .WithTrackingName(TrackingNames.Permissions);

        var roles = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RequireRoleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateGuardSet(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .WithTrackingName(TrackingNames.Roles);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = permissions.Combine(roles).Combine(modules).Combine(trigger)
            .WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (((permissionSets, roleSets), modules), hasTrigger) = source;
            if (!hasTrigger)
            {
                return;
            }

            Emit(spc, permissionSets, roleSets, modules);
        });
    }

    private static GuardSet? CreateGuardSet(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var values = ImmutableArray.CreateBuilder<string>();
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
        return new GuardSet(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            values.ToImmutable().ToEquatableArray(),
            LocationInfo.From(type));
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<GuardSet> permissions,
        ImmutableArray<GuardSet> roles,
        EquatableArray<ModuleScanner.Module> modules)
    {
        var modulePermissions = modules.ToDictionary(module => module, _ => new SortedSet<string>(StringComparer.Ordinal));
        var moduleRoles = modules.ToDictionary(module => module, _ => new SortedSet<string>(StringComparer.Ordinal));
        var reportedUnmatched = new HashSet<string>(StringComparer.Ordinal);

        void Assign(ImmutableArray<GuardSet> guards, Dictionary<ModuleScanner.Module, SortedSet<string>> target)
        {
            foreach (var guard in guards)
            {
                var module = FindBestModule(guard.Namespace, modules);
                if (module is null)
                {
                    if (reportedUnmatched.Add(guard.HandlerFqn))
                    {
                        spc.ReportDiagnostic(DiagnosticInfo
                            .Create(RequirementNotInModule, guard.Location, guard.HandlerFqn)
                            .ToDiagnostic());
                    }

                    continue;
                }

                foreach (var value in guard.Values)
                {
                    target[module].Add(value);
                }
            }
        }

        Assign(permissions, modulePermissions);
        Assign(roles, moduleRoles);

        foreach (var module in modules.OrderBy(module => module.Name, StringComparer.Ordinal))
        {
            var perms = modulePermissions[module];
            var roleNames = moduleRoles[module];
            if (perms.Count == 0 && roleNames.Count == 0)
            {
                continue;
            }

            EmitModule(spc, module, perms, roleNames);
        }
    }

    private static void EmitModule(
        SourceProductionContext spc,
        ModuleScanner.Module module,
        SortedSet<string> permissions,
        SortedSet<string> roles)
    {
        var moduleName = module.Name;

        var sb = new StringBuilder();
        sb.AppendLine("using Elarion.Abstractions.Authorization;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        if (module.Namespace.Length > 0)
        {
            sb.AppendLine($"namespace {module.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Extension methods for contributing the {moduleName} module's permission catalog.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public static class {moduleName}PermissionCatalogExtensions");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(
            $"    /// Registers the {moduleName} module's [RequirePermission]/[RequireRole] strings as a PermissionCatalogModule.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public static IServiceCollection Add{moduleName}Permissions(");
        sb.AppendLine("        this IServiceCollection services)");
        sb.AppendLine("    {");
        sb.AppendLine($"        services.AddSingleton(new {PermissionCatalogModuleFqn}");
        sb.AppendLine("        {");
        sb.AppendLine($"            Module = {SymbolDisplay.FormatLiteral(moduleName, quote: true)},");
        sb.AppendLine($"            Permissions = {StringArrayLiteral(permissions)},");
        sb.AppendLine($"            Roles = {StringArrayLiteral(roles)},");
        sb.AppendLine("        });");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource(
            $"{moduleName}PermissionCatalogExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

        var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
        ModuleDefaultsEmitter.EmitFiller(
            spc,
            module.Namespace,
            module.TypeName,
            ModuleDefaultsEmitter.AddPermissionsMethod,
            "Permissions",
            $"{nsPrefix}{moduleName}PermissionCatalogExtensions.Add{moduleName}Permissions(services);");
    }

    private static string StringArrayLiteral(SortedSet<string> values)
    {
        if (values.Count == 0)
        {
            return "global::System.Array.Empty<string>()";
        }

        var literals = values.Select(value => SymbolDisplay.FormatLiteral(value, quote: true));
        return "new string[] { " + string.Join(", ", literals) + " }";
    }

    private static ModuleScanner.Module? FindBestModule(string handlerNamespace, EquatableArray<ModuleScanner.Module> modules)
    {
        ModuleScanner.Module? best = null;
        foreach (var module in modules)
        {
            if (ModuleScanner.IsInScope(handlerNamespace, module.Namespace) &&
                (best is null || module.Namespace.Length > best.Namespace.Length))
            {
                best = module;
            }
        }

        return best;
    }
}
