using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the permission catalog from every <c>[RequirePermission("…")]</c>/<c>[RequireRole("…")]</c> on a
/// handler. Two surfaces, both with zero central-list maintenance:
/// <list type="bullet">
/// <item>the runtime <c>IPermissionCatalog</c> — one <c>PermissionCatalogModule</c> per module registered through
/// the module's gated <c>ConfigureDefaultServices</c> (cross-assembly via DI);</item>
/// <item>the compile-time <c>ElarionPermissions</c> static (<c>All</c>/<c>Roles</c>/<c>ByModule</c>/<c>ByKind</c> +
/// typed accessors), emitted into the assembly's root namespace and aggregated cross-assembly from the Elarion
/// manifest, so static role policy can reference <c>ElarionPermissions.All</c>.</item>
/// </list>
/// <para>Trigger: <c>[assembly: UseElarion]</c> or <c>[assembly: GeneratePermissionCatalog]</c>.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PermissionCatalogGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GeneratePermissionCatalogAttribute";
    private const string PermissionCatalogModuleFqn = "global::Elarion.Abstractions.Authorization.PermissionCatalogModule";
    private const string PermissionCatalogEntryFqn = "global::Elarion.Abstractions.Authorization.PermissionCatalogEntry";
    private const string PermissionKindFqn = "global::Elarion.Abstractions.Authorization.PermissionKind";
    private const string ReadOnlyListFqn = "global::System.Collections.Generic.IReadOnlyList<string>";

    private static readonly DiagnosticDescriptor RequirementNotInModule = new(
        "ELPERM001",
        "Authorization requirement is not in any module",
        "Handler '{0}' declares authorization requirements but is not under any [AppModule] namespace, so they "
        + "are not added to the runtime permission catalog; move it under a module",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateTypedAccessor = new(
        "ELPERM002",
        "Permission produces a duplicate typed accessor",
        "Permissions '{0}' and '{1}' map to the same ElarionPermissions accessor '{2}'; the second is omitted from "
        + "the typed accessors (both remain in ElarionPermissions.All)",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static class TrackingNames
    {
        public const string Permissions = "PermissionCatalogPermissions";
        public const string Roles = "PermissionCatalogRoles";
        public const string PerModule = "PermissionCatalogPerModule";
        public const string Static = "PermissionCatalogStatic";
    }

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var permissions = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequirePermissionAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadPermissions(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .WithTrackingName(TrackingNames.Permissions);

        var roles = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequireRoleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadRoles(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .WithTrackingName(TrackingNames.Roles);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        // Runtime catalog: per-module DI registrations wired into ConfigureDefaultServices (current assembly only;
        // referenced assemblies register their own).
        var perModule = permissions.Combine(roles).Combine(modules).Combine(trigger)
            .WithTrackingName(TrackingNames.PerModule);
        context.RegisterSourceOutput(perModule, static (spc, source) =>
        {
            var (((permissionGuards, roleGuards), moduleList), hasTrigger) = source;
            if (hasTrigger)
                EmitPerModule(spc, permissionGuards, roleGuards, moduleList);
        });

        // Compile-time ElarionPermissions static: aggregates this assembly's requirements with referenced
        // assemblies' via the Elarion manifest, emitted into the root namespace for static role policy to reference.
        var manifests = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ElarionManifestReader.Read(reference, ct))
            .Collect();
        var rootNamespace = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? string.Empty)
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) ? ns : null))
            .Select(static (pair, _) => string.IsNullOrEmpty(pair.Right) ? pair.Left : pair.Right!);

        var staticInput = permissions.Combine(roles).Combine(modules).Combine(manifests).Combine(rootNamespace)
            .Combine(trigger)
            .WithTrackingName(TrackingNames.Static);
        context.RegisterSourceOutput(staticInput, static (spc, source) =>
        {
            var (((((permissionGuards, roleGuards), moduleList), manifestList), ns), hasTrigger) = source;
            if (hasTrigger)
                EmitStatic(spc, permissionGuards, roleGuards, moduleList, manifestList, ns);
        });
    }

    // --- Runtime catalog (per-module DI) ---

    private static void EmitPerModule(
        SourceProductionContext spc,
        ImmutableArray<PermissionDiscovery.PermissionGuard> permissions,
        ImmutableArray<PermissionDiscovery.RoleGuard> roles,
        EquatableArray<ModuleScanner.Module> modules)
    {
        var modulePermissions = modules.ToDictionary(
            module => module, _ => new SortedDictionary<string, string>(StringComparer.Ordinal));
        var moduleRoles = modules.ToDictionary(module => module, _ => new SortedSet<string>(StringComparer.Ordinal));
        var reportedUnmatched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var guard in permissions)
        {
            var module = FindBestModule(guard.Namespace, modules);
            if (module is null)
            {
                ReportUnmatched(spc, reportedUnmatched, guard.HandlerFqn, guard.Location);
                continue;
            }

            foreach (var value in guard.Values)
                modulePermissions[module][value.Value] = value.Kind;
        }

        foreach (var guard in roles)
        {
            var module = FindBestModule(guard.Namespace, modules);
            if (module is null)
            {
                ReportUnmatched(spc, reportedUnmatched, guard.HandlerFqn, guard.Location);
                continue;
            }

            foreach (var value in guard.Values)
                moduleRoles[module].Add(value);
        }

        foreach (var module in modules.OrderBy(module => module.Name, StringComparer.Ordinal))
        {
            var perms = modulePermissions[module];
            var roleNames = moduleRoles[module];
            if (perms.Count == 0 && roleNames.Count == 0)
                continue;

            EmitModuleRegistration(spc, module, perms, roleNames);
        }
    }

    private static void ReportUnmatched(
        SourceProductionContext spc, HashSet<string> reported, string handlerFqn, LocationInfo location)
    {
        if (reported.Add(handlerFqn))
            spc.ReportDiagnostic(DiagnosticInfo.Create(RequirementNotInModule, location, handlerFqn).ToDiagnostic());
    }

    private static void EmitModuleRegistration(
        SourceProductionContext spc,
        ModuleScanner.Module module,
        SortedDictionary<string, string> permissions,
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
        sb.AppendLine($"            Module = {Literal(moduleName)},");
        sb.AppendLine($"            Permissions = new {PermissionCatalogEntryFqn}[]");
        sb.AppendLine("            {");
        foreach (var entry in permissions)
            sb.AppendLine(
                $"                new {PermissionCatalogEntryFqn} {{ Name = {Literal(entry.Key)}, Kind = {PermissionKindFqn}.{entry.Value} }},");
        sb.AppendLine("            },");
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

    // --- Compile-time ElarionPermissions static ---

    private sealed record ResolvedPermission(string Value, string Kind, string? Module);

    private static void EmitStatic(
        SourceProductionContext spc,
        ImmutableArray<PermissionDiscovery.PermissionGuard> permissions,
        ImmutableArray<PermissionDiscovery.RoleGuard> roles,
        EquatableArray<ModuleScanner.Module> currentModules,
        ImmutableArray<ElarionManifest.Data> manifests,
        string targetNamespace)
    {
        var manifest = ElarionManifest.Data.Combine(manifests);

        // Modules to resolve against: this assembly's plus every referenced assembly's (from the manifest).
        var moduleScopes = new List<(string Name, string Namespace)>();
        foreach (var module in currentModules)
            moduleScopes.Add((module.Name, module.Namespace));
        foreach (var module in manifest.Modules)
            moduleScopes.Add((module.ModuleName, module.Namespace));

        // Every requirement (this assembly via syntax, referenced assemblies via manifest), resolved to its module.
        var resolved = new List<ResolvedPermission>();
        foreach (var guard in permissions)
        foreach (var value in guard.Values)
            resolved.Add(new ResolvedPermission(value.Value, value.Kind, FindBestModule(guard.Namespace, moduleScopes)));
        foreach (var permission in manifest.Permissions)
            resolved.Add(new ResolvedPermission(permission.Value, permission.Kind, FindBestModule(permission.Namespace, moduleScopes)));

        var roleSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var guard in roles)
        foreach (var value in guard.Values)
            roleSet.Add(value);
        foreach (var role in manifest.Roles)
            roleSet.Add(role.Value);

        if (resolved.Count == 0 && roleSet.Count == 0)
            return;

        var all = new SortedSet<string>(resolved.Select(p => p.Value), StringComparer.Ordinal);

        var byModule = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var permission in resolved)
        {
            if (permission.Module is null)
                continue;
            if (!byModule.TryGetValue(permission.Module, out var set))
                byModule[permission.Module] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(permission.Value);
        }

        var byKind = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var permission in resolved)
        {
            if (!byKind.TryGetValue(permission.Kind, out var set))
                byKind[permission.Kind] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(permission.Value);
        }

        var accessors = BuildAccessors(all, spc);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.PermissionCatalogGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (targetNamespace.Length > 0)
        {
            sb.AppendLine($"namespace {targetNamespace};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The compile-time catalog of every [RequirePermission]/[RequireRole] declared across the");
        sb.AppendLine("/// application (and referenced module assemblies). Reference it from static role policy so the");
        sb.AppendLine("/// permission lists are never hand-maintained.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static partial class ElarionPermissions");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>All distinct permission strings, ordinally sorted.</summary>");
        sb.AppendLine($"    public static {ReadOnlyListFqn} All {{ get; }} = {StringArrayLiteral(all)};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>All distinct role names, ordinally sorted.</summary>");
        sb.AppendLine($"    public static {ReadOnlyListFqn} Roles {{ get; }} = {StringArrayLiteral(roleSet)};");
        sb.AppendLine();
        AppendStringMap(sb, "ByModule", "Permissions grouped by owning module.", "string", byModule, static key => Literal(key));
        sb.AppendLine();
        AppendStringMap(
            sb, "ByKind", "Permissions grouped by declared PermissionKind.", PermissionKindFqn, byKind,
            static key => $"{PermissionKindFqn}.{key}");
        AppendAccessors(sb, accessors);
        sb.AppendLine("}");

        spc.AddSource("ElarionPermissions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendStringMap(
        StringBuilder sb,
        string propertyName,
        string summary,
        string keyType,
        SortedDictionary<string, SortedSet<string>> map,
        Func<string, string> keyLiteral)
    {
        var dictType =
            $"global::System.Collections.Generic.IReadOnlyDictionary<{keyType}, {ReadOnlyListFqn}>";
        var concreteType =
            $"global::System.Collections.Generic.Dictionary<{keyType}, {ReadOnlyListFqn}>";
        sb.AppendLine($"    /// <summary>{summary}</summary>");
        sb.AppendLine($"    public static {dictType} {propertyName} {{ get; }} = new {concreteType}");
        sb.AppendLine("    {");
        foreach (var pair in map)
            sb.AppendLine($"        [{keyLiteral(pair.Key)}] = {StringArrayLiteral(pair.Value)},");
        sb.AppendLine("    };");
    }

    // --- Typed accessors ---

    private sealed class AccessorTree
    {
        public SortedDictionary<string, string> TopLevel { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, SortedDictionary<string, string>> Groups { get; } = new(StringComparer.Ordinal);
    }

    private static AccessorTree BuildAccessors(IEnumerable<string> values, SourceProductionContext spc)
    {
        var tree = new AccessorTree();
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal); // path -> value

        foreach (var value in values)
        {
            var segments = value
                .Split(new[] { '.', ':', '/', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Pascal)
                .Where(segment => segment.Length > 0)
                .ToArray();
            if (segments.Length == 0)
                continue;

            string path;
            string group;
            string member;
            if (segments.Length == 1)
            {
                group = string.Empty;
                member = segments[0];
                path = member;
            }
            else
            {
                group = segments[0];
                member = string.Concat(segments.Skip(1));
                path = group + "." + member;
            }

            if (claimed.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing, value, StringComparison.Ordinal))
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateTypedAccessor, Location.None, existing, value, path));
                continue;
            }

            claimed[path] = value;
            if (group.Length == 0)
                tree.TopLevel[member] = value;
            else
            {
                if (!tree.Groups.TryGetValue(group, out var members))
                    tree.Groups[group] = members = new SortedDictionary<string, string>(StringComparer.Ordinal);
                members[member] = value;
            }
        }

        // A nested class and a const at the same level cannot share a name; drop the const, keep the class.
        foreach (var group in tree.Groups.Keys)
            tree.TopLevel.Remove(group);

        return tree;
    }

    private static void AppendAccessors(StringBuilder sb, AccessorTree tree)
    {
        foreach (var entry in tree.TopLevel)
            sb.AppendLine($"    public const string {entry.Key} = {Literal(entry.Value)};");

        foreach (var groupEntry in tree.Groups)
        {
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Permissions under the '{groupEntry.Key}' prefix.</summary>");
            sb.AppendLine($"    public static class {groupEntry.Key}");
            sb.AppendLine("    {");
            foreach (var memberEntry in groupEntry.Value)
                sb.AppendLine($"        public const string {memberEntry.Key} = {Literal(memberEntry.Value)};");
            sb.AppendLine("    }");
        }
    }

    private static string Pascal(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        var upperNext = true;
        foreach (var ch in segment)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        if (sb.Length > 0 && char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    // --- Shared helpers ---

    private static ModuleScanner.Module? FindBestModule(string handlerNamespace, EquatableArray<ModuleScanner.Module> modules)
    {
        ModuleScanner.Module? best = null;
        foreach (var module in modules)
        {
            if (ModuleScanner.IsInScope(handlerNamespace, module.Namespace) &&
                (best is null || module.Namespace.Length > best.Namespace.Length))
                best = module;
        }

        return best;
    }

    private static string? FindBestModule(string handlerNamespace, List<(string Name, string Namespace)> modules)
    {
        string? bestName = null;
        var bestLength = -1;
        foreach (var (name, ns) in modules)
        {
            if (ModuleScanner.IsInScope(handlerNamespace, ns) && ns.Length > bestLength)
            {
                bestName = name;
                bestLength = ns.Length;
            }
        }

        return bestName;
    }

    private static string StringArrayLiteral(IEnumerable<string> values)
    {
        var literals = values.Select(Literal).ToArray();
        return literals.Length == 0
            ? "global::System.Array.Empty<string>()"
            : "new string[] { " + string.Join(", ", literals) + " }";
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
