using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Emits compact assembly-level manifests for modules and transport handlers in the current compilation. Host-side
/// generators consume these manifests from references instead of recursively scanning referenced symbols.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionManifestGenerator : IIncrementalGenerator {
    private const string AppModuleAttributeMetadataName = ElarionGeneratorConventions.AppModuleAttribute;
    private const string ModuleEndpointsAttributeMetadataName = ElarionGeneratorConventions.ModuleEndpointsAttribute;
    private const string ResourceFilterAttributeMetadataName = ElarionGeneratorConventions.ResourceFilterAttribute;

    /// <summary>
    /// A <c>[ModuleEndpoints]</c> class with no recognized hook contributes nothing — the attribute is dead
    /// weight, most likely a signature mistake (the hooks must be <c>static</c> with exactly one parameter).
    /// </summary>
    public static readonly DiagnosticDescriptor ModuleEndpointsWithoutHooks = new(
        "ELMOD005",
        "[ModuleEndpoints] class declares no endpoint hook",
        "Class '{0}' is annotated with [ModuleEndpoints(\"{1}\")] but declares neither a static "
        + "MapEndpoints(IEndpointRouteBuilder) nor a static ConfigureEndpointGroup(IEndpointRouteBuilder) hook; "
        + "it contributes nothing to the module",
        "Elarion.Modules",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>A discovered item plus the (value-equatable) diagnostics its transform produced — diagnostics
    /// are data (ADR-0006): raw <see cref="Diagnostic"/>s pin syntax trees and defeat caching.</summary>
    private sealed record ManifestItem<T>(T? Model, EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>The fully-built manifest output: the final source text plus its diagnostics, both value-equatable,
    /// so the source-output stage is skipped whenever no discovered input actually changed.</summary>
    private sealed record ManifestOutput(string? Source, EquatableArray<DiagnosticInfo> Diagnostics);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AppModuleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => AppModuleDiscovery.CreateModule(ctx))
            .Where(static module => module is not null)
            .Select(static (module, _) => module!)
            .Collect()
            .Select(static (entries, _) => entries
                .OrderBy(static m => m.ModuleName, StringComparer.Ordinal)
                .ThenBy(static m => m.TypeFqn, StringComparer.Ordinal)
                .ToEquatableArray())
            .WithTrackingName("ManifestModules");

        var moduleEndpointHooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModuleEndpointsAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateModuleEndpoints(ctx))
            .Where(static item => item.Model is not null || !item.Diagnostics.IsEmpty)
            .Collect()
            .Select(static (items, _) => items.ToEquatableArray())
            .WithTrackingName("ManifestModuleEndpoints");

        var httpEndpoints = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                HttpEndpointEmission.HttpEndpointAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => CreateHttpEndpoint(ctx, ct))
            .Where(static item => item.Model is not null || !item.Diagnostics.IsEmpty)
            .Collect()
            .Select(static (items, _) => items.ToEquatableArray())
            .WithTrackingName("ManifestHttpEndpoints");

        var rpcMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RpcMethodEmission.HandlerAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => CreateRpcMethod(ctx, ct))
            .Where(static item => item.Model is not null || !item.Diagnostics.IsEmpty)
            .Collect()
            .Select(static (items, _) => items.ToEquatableArray())
            .WithTrackingName("ManifestRpcMethods");

        var resourceFilters = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ResourceFilterAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ResourceFilterDiscovery.CreateResourceFilter(ctx))
            .Where(static filter => filter is not null)
            .Select(static (filter, _) => filter!)
            .Collect()
            .Select(static (filters, _) => filters.ToEquatableArray())
            .WithTrackingName("ManifestResourceFilters");

        var permissions = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequirePermissionAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadPermissions(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .Select(static (guards, _) => guards.ToEquatableArray())
            .WithTrackingName("ManifestPermissions");

        var roles = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequireRoleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadRoles(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect()
            .Select(static (guards, _) => guards.ToEquatableArray())
            .WithTrackingName("ManifestRoles");

        var featureVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, false))
            .Collect()
            .Select(static (groups, _) => groups.ToEquatableArray())
            .WithTrackingName("ManifestFeatureVariants");

        var configurationVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.ConfigurationVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, true))
            .Collect()
            .Select(static (groups, _) => groups.ToEquatableArray())
            .WithTrackingName("ManifestConfigurationVariants");

        var output = modules.Combine(moduleEndpointHooks).Combine(httpEndpoints).Combine(rpcMethods)
            .Combine(resourceFilters).Combine(permissions).Combine(roles).Combine(featureVariants)
            .Combine(configurationVariants)
            .Select(static (source, ct) => {
                var ((((((((moduleEntries, moduleEndpointsItems), httpEndpointEntries), rpcMethodEntries),
                    resourceFilterEntries), permissionGuards), roleGuards), featureVariantGroups),
                    configurationVariantGroups) = source;
                ct.ThrowIfCancellationRequested();
                return BuildManifestOutput(
                    moduleEntries, moduleEndpointsItems, httpEndpointEntries, rpcMethodEntries,
                    resourceFilterEntries, permissionGuards, roleGuards,
                    featureVariantGroups, configurationVariantGroups);
            })
            .WithTrackingName("Manifest");

        context.RegisterSourceOutput(output, static (spc, result) => {
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (result.Source is not null)
                spc.AddSource("ElarionManifest.g.cs", SourceText.From(result.Source, Encoding.UTF8));
        });
    }

    // Variant discovery (one manifest entry per service contract) is shared with the variant-catalog
    // generator via VariantDiscovery, so the manifest and the registry cannot drift. Resource-filter
    // discovery is shared with the bootstrapper via ResourceFilterDiscovery for the same reason.

    // A [ModuleEndpoints] contributor: the named module plus which convention hooks the class declares. A class
    // with no usable hook is reported (ELMOD005) and not published — an empty entry could never map anything.
    private static ManifestItem<ElarionManifest.ModuleEndpoints> CreateModuleEndpoints(
        GeneratorAttributeSyntaxContext ctx) {
        var hooks = AppModuleDiscovery.CreateModuleEndpoints(ctx);
        if (hooks is null)
            return new ManifestItem<ElarionManifest.ModuleEndpoints>(null, EquatableArray<DiagnosticInfo>.Empty);

        if (!hooks.HasMapEndpoints && !hooks.HasConfigureEndpointGroup)
            return new ManifestItem<ElarionManifest.ModuleEndpoints>(
                null,
                new[] {
                    DiagnosticInfo.Create(
                        ModuleEndpointsWithoutHooks,
                        ctx.TargetSymbol.Locations.FirstOrDefault(),
                        ctx.TargetSymbol.ToDisplayString(),
                        hooks.ModuleName)
                }.ToEquatableArray());

        return new ManifestItem<ElarionManifest.ModuleEndpoints>(hooks, EquatableArray<DiagnosticInfo>.Empty);
    }

    private static ManifestItem<HttpEndpointEmission.Model> CreateHttpEndpoint(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct) {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var model = HttpEndpointEmission.CreateModel(ctx, diagnostics.Add, ct);
        return new ManifestItem<HttpEndpointEmission.Model>(model, diagnostics.ToImmutable().ToEquatableArray());
    }

    private static ManifestItem<RpcMethodEmission.Model> CreateRpcMethod(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct) {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var model = RpcMethodEmission.CreateModel(ctx, diagnostics.Add, ct);
        return new ManifestItem<RpcMethodEmission.Model>(model, diagnostics.ToImmutable().ToEquatableArray());
    }

    private static ManifestOutput BuildManifestOutput(
        EquatableArray<ElarionManifest.Module> moduleEntries,
        EquatableArray<ManifestItem<ElarionManifest.ModuleEndpoints>> moduleEndpointsItems,
        EquatableArray<ManifestItem<HttpEndpointEmission.Model>> httpEndpointItems,
        EquatableArray<ManifestItem<RpcMethodEmission.Model>> rpcMethodItems,
        EquatableArray<ElarionManifest.ResourceFilter> resourceFilters,
        EquatableArray<PermissionDiscovery.PermissionGuard> permissionGuards,
        EquatableArray<PermissionDiscovery.RoleGuard> roleGuards,
        EquatableArray<EquatableArray<ElarionManifest.Variant>> featureVariantGroups,
        EquatableArray<EquatableArray<ElarionManifest.Variant>> configurationVariantGroups) {
        var diagnostics = new List<DiagnosticInfo>();
        foreach (var item in moduleEndpointsItems)
        foreach (var diagnostic in item.Diagnostics)
            diagnostics.Add(diagnostic);

        foreach (var item in httpEndpointItems)
        foreach (var diagnostic in item.Diagnostics)
            diagnostics.Add(diagnostic);

        foreach (var item in rpcMethodItems)
        foreach (var diagnostic in item.Diagnostics)
            diagnostics.Add(diagnostic);

        // Same-compilation duplicate module names are reported here — this generator always runs — and only
        // the deterministic winner is published, so downstream manifest consumers never see the duplicate.
        var modules = ModuleScanner.DeduplicateByName(
            moduleEntries, static m => m.ModuleName, static m => m.TypeFqn, diagnostics);

        var moduleEndpointHooks = moduleEndpointsItems
            .Where(static item => item.Model is not null)
            .Select(static item => item.Model!)
            .ToArray();
        var httpEndpoints = httpEndpointItems
            .Where(static item => item.Model is not null)
            .Select(static item => item.Model!)
            .ToArray();
        var rpcMethods = rpcMethodItems
            .Where(static item => item.Model is not null)
            .Select(static item => item.Model!)
            .ToArray();

        var permissionSet = new HashSet<ElarionManifest.Permission>();
        foreach (var guard in permissionGuards)
        foreach (var value in guard.Values)
            permissionSet.Add(new ElarionManifest.Permission(guard.Namespace, value.Resource, value.Verb));
        var permissions = permissionSet
            .OrderBy(static p => p.Resource, StringComparer.Ordinal)
            .ThenBy(static p => p.Verb, StringComparer.Ordinal)
            .ThenBy(static p => p.Namespace, StringComparer.Ordinal)
            .ToArray();

        var roleSet = new HashSet<ElarionManifest.Role>();
        foreach (var guard in roleGuards)
        foreach (var value in guard.Values)
            roleSet.Add(new ElarionManifest.Role(guard.Namespace, value));
        var roles = roleSet
            .OrderBy(static r => r.Value, StringComparer.Ordinal)
            .ThenBy(static r => r.Namespace, StringComparer.Ordinal)
            .ToArray();

        var variantSet = new HashSet<ElarionManifest.Variant>();
        foreach (var group in featureVariantGroups)
        foreach (var variant in group)
            variantSet.Add(variant);
        foreach (var group in configurationVariantGroups)
        foreach (var variant in group)
            variantSet.Add(variant);
        var variants = variantSet
            .OrderBy(static v => v.SelectorKey, StringComparer.Ordinal)
            .ThenBy(static v => v.ContractFqn, StringComparer.Ordinal)
            .ThenBy(static v => v.Value, StringComparer.Ordinal)
            .ThenBy(static v => v.Namespace, StringComparer.Ordinal)
            .ToArray();

        if (modules.Count == 0 && moduleEndpointHooks.Length == 0 && httpEndpoints.Length == 0
            && rpcMethods.Length == 0 && resourceFilters.IsEmpty && permissions.Length == 0 && roles.Length == 0
            && variants.Length == 0)
            return new ManifestOutput(null, diagnostics.ToEquatableArray());

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ElarionManifestGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        AppendAssemblyMetadata(sb, ElarionManifest.SchemaKey, ElarionManifest.SchemaVersion);

        foreach (var module in modules.OrderBy(static m => m.ModuleName, StringComparer.Ordinal)
                     .ThenBy(static m => m.TypeFqn, StringComparer.Ordinal))
            AppendAssemblyMetadata(sb, ElarionManifest.ModuleKey, ElarionManifest.EncodeModule(module));

        foreach (var hooks in moduleEndpointHooks.OrderBy(static h => h.ModuleName, StringComparer.Ordinal)
                     .ThenBy(static h => h.TypeFqn, StringComparer.Ordinal))
            AppendAssemblyMetadata(sb, ElarionManifest.ModuleEndpointsKey,
                ElarionManifest.EncodeModuleEndpoints(hooks));

        foreach (var endpoint in httpEndpoints.OrderBy(static e => e.Verb, StringComparer.Ordinal)
                     .ThenBy(static e => e.Route, StringComparer.Ordinal)
                     .ThenBy(static e => e.EndpointName, StringComparer.Ordinal))
            AppendAssemblyMetadata(sb, ElarionManifest.HttpEndpointKey, ElarionManifest.EncodeHttpEndpoint(endpoint));

        foreach (var method in rpcMethods.OrderBy(static m => m.MethodName, StringComparer.Ordinal)
                     .ThenBy(static m => m.RequestTypeFqn, StringComparer.Ordinal))
            AppendAssemblyMetadata(sb, ElarionManifest.RpcMethodKey, ElarionManifest.EncodeRpcMethod(method));

        foreach (var filter in resourceFilters.OrderBy(static f => f.SpecFqn, StringComparer.Ordinal))
            AppendAssemblyMetadata(sb, ElarionManifest.ResourceFilterKey, ElarionManifest.EncodeResourceFilter(filter));

        foreach (var permission in permissions)
            AppendAssemblyMetadata(sb, ElarionManifest.PermissionKey, ElarionManifest.EncodePermission(permission));

        foreach (var role in roles)
            AppendAssemblyMetadata(sb, ElarionManifest.RoleKey, ElarionManifest.EncodeRole(role));

        foreach (var variant in variants)
            AppendAssemblyMetadata(sb, ElarionManifest.VariantKey, ElarionManifest.EncodeVariant(variant));

        return new ManifestOutput(sb.ToString(), diagnostics.ToEquatableArray());
    }

    private static void AppendAssemblyMetadata(StringBuilder sb, string key, string value) {
        sb.Append("[assembly: global::System.Reflection.AssemblyMetadataAttribute(");
        sb.Append(SymbolDisplay.FormatLiteral(key, true));
        sb.Append(", ");
        sb.Append(SymbolDisplay.FormatLiteral(value, true));
        sb.AppendLine(")]");
    }
}
