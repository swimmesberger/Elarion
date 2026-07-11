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
public sealed class ElarionManifestGenerator : IIncrementalGenerator
{
    private const string AppModuleAttributeMetadataName = ElarionGeneratorConventions.AppModuleAttribute;
    private const string ModuleEndpointsAttributeMetadataName = ElarionGeneratorConventions.ModuleEndpointsAttribute;
    private const string ResourceFilterAttributeMetadataName = ElarionGeneratorConventions.ResourceFilterAttribute;

    /// <summary>
    /// A <c>[ModuleEndpoints]</c> class with no recognized hook contributes nothing — the attribute is dead
    /// weight, most likely a signature mistake (the hooks must be <c>static</c> with exactly one parameter).
    /// </summary>
    public static readonly DiagnosticDescriptor ModuleEndpointsWithoutHooks = new(
        id: "ELMOD005",
        title: "[ModuleEndpoints] class declares no endpoint hook",
        messageFormat:
        "Class '{0}' is annotated with [ModuleEndpoints(\"{1}\")] but declares neither a static "
        + "MapEndpoints(IEndpointRouteBuilder) nor a static ConfigureEndpointGroup(IEndpointRouteBuilder) hook; "
        + "it contributes nothing to the module",
        category: "Elarion.Modules",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record ManifestItem<T>(T? Model, ImmutableArray<Diagnostic> Diagnostics);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AppModuleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => AppModuleDiscovery.CreateModule(ctx))
            .Where(static module => module is not null)
            .Select(static (module, _) => module!)
            .Collect();

        var moduleEndpointHooks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModuleEndpointsAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateModuleEndpoints(ctx))
            .Where(static item => item.Model is not null || item.Diagnostics.Length > 0)
            .Collect();

        var httpEndpoints = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                HttpEndpointEmission.HttpEndpointAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => CreateHttpEndpoint(ctx, ct))
            .Where(static item => item.Model is not null || item.Diagnostics.Length > 0)
            .Collect();

        var rpcMethods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RpcMethodEmission.HandlerAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => CreateRpcMethod(ctx, ct))
            .Where(static item => item.Model is not null || item.Diagnostics.Length > 0)
            .Collect();

        var resourceFilters = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ResourceFilterAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ResourceFilterDiscovery.CreateResourceFilter(ctx))
            .Where(static filter => filter is not null)
            .Select(static (filter, _) => filter!)
            .Collect();

        var permissions = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequirePermissionAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadPermissions(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect();

        var roles = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                PermissionDiscovery.RequireRoleAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => PermissionDiscovery.ReadRoles(ctx))
            .Where(static guard => guard is not null)
            .Select(static (guard, _) => guard!)
            .Collect();

        var featureVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, isConfiguration: false))
            .Collect();

        var configurationVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.ConfigurationVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, isConfiguration: true))
            .Collect();

        var combined = modules.Combine(moduleEndpointHooks).Combine(httpEndpoints).Combine(rpcMethods)
            .Combine(resourceFilters).Combine(permissions).Combine(roles).Combine(featureVariants)
            .Combine(configurationVariants);
        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((((((((moduleEntries, moduleEndpointsItems), httpEndpointEntries), rpcMethodEntries), resourceFilterEntries), permissionGuards), roleGuards), featureVariantGroups), configurationVariantGroups) = source;
            EmitManifest(
                spc, moduleEntries, moduleEndpointsItems, httpEndpointEntries, rpcMethodEntries,
                resourceFilterEntries, permissionGuards, roleGuards,
                featureVariantGroups.AddRange(configurationVariantGroups));
        });
    }

    // Variant discovery (one manifest entry per service contract) is shared with the variant-catalog
    // generator via VariantDiscovery, so the manifest and the registry cannot drift. Resource-filter
    // discovery is shared with the bootstrapper via ResourceFilterDiscovery for the same reason.

    // A [ModuleEndpoints] contributor: the named module plus which convention hooks the class declares. A class
    // with no usable hook is reported (ELMOD005) and not published — an empty entry could never map anything.
    private static ManifestItem<ElarionManifest.ModuleEndpoints> CreateModuleEndpoints(
        GeneratorAttributeSyntaxContext ctx)
    {
        var hooks = AppModuleDiscovery.CreateModuleEndpoints(ctx);
        if (hooks is null)
            return new ManifestItem<ElarionManifest.ModuleEndpoints>(null, ImmutableArray<Diagnostic>.Empty);

        if (!hooks.HasMapEndpoints && !hooks.HasConfigureEndpointGroup)
        {
            return new ManifestItem<ElarionManifest.ModuleEndpoints>(
                null,
                [
                    Diagnostic.Create(
                        ModuleEndpointsWithoutHooks,
                        ctx.TargetSymbol.Locations.FirstOrDefault() ?? Location.None,
                        ctx.TargetSymbol.ToDisplayString(),
                        hooks.ModuleName),
                ]);
        }

        return new ManifestItem<ElarionManifest.ModuleEndpoints>(hooks, ImmutableArray<Diagnostic>.Empty);
    }

    private static ManifestItem<HttpEndpointEmission.Model> CreateHttpEndpoint(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var model = HttpEndpointEmission.CreateModel(ctx, diagnostics.Add, ct);
        return new ManifestItem<HttpEndpointEmission.Model>(model, diagnostics.ToImmutable());
    }

    private static ManifestItem<RpcMethodEmission.Model> CreateRpcMethod(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var model = RpcMethodEmission.CreateModel(ctx, diagnostics.Add, ct);
        return new ManifestItem<RpcMethodEmission.Model>(model, diagnostics.ToImmutable());
    }

    private static void EmitManifest(
        SourceProductionContext spc,
        ImmutableArray<ElarionManifest.Module> modules,
        ImmutableArray<ManifestItem<ElarionManifest.ModuleEndpoints>> moduleEndpointsItems,
        ImmutableArray<ManifestItem<HttpEndpointEmission.Model>> httpEndpointItems,
        ImmutableArray<ManifestItem<RpcMethodEmission.Model>> rpcMethodItems,
        ImmutableArray<ElarionManifest.ResourceFilter> resourceFilters,
        ImmutableArray<PermissionDiscovery.PermissionGuard> permissionGuards,
        ImmutableArray<PermissionDiscovery.RoleGuard> roleGuards,
        ImmutableArray<EquatableArray<ElarionManifest.Variant>> variantGroups)
    {
        foreach (var item in moduleEndpointsItems)
        {
            foreach (var diagnostic in item.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
        }

        foreach (var item in httpEndpointItems)
        {
            foreach (var diagnostic in item.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
        }

        foreach (var item in rpcMethodItems)
        {
            foreach (var diagnostic in item.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
        }

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
        foreach (var group in variantGroups)
        foreach (var variant in group)
            variantSet.Add(variant);
        var variants = variantSet
            .OrderBy(static v => v.SelectorKey, StringComparer.Ordinal)
            .ThenBy(static v => v.ContractFqn, StringComparer.Ordinal)
            .ThenBy(static v => v.Value, StringComparer.Ordinal)
            .ThenBy(static v => v.Namespace, StringComparer.Ordinal)
            .ToArray();

        if (modules.Length == 0 && moduleEndpointHooks.Length == 0 && httpEndpoints.Length == 0
            && rpcMethods.Length == 0 && resourceFilters.Length == 0 && permissions.Length == 0 && roles.Length == 0
            && variants.Length == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ElarionManifestGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        AppendAssemblyMetadata(sb, ElarionManifest.SchemaKey, ElarionManifest.SchemaVersion);

        foreach (var module in modules.OrderBy(static m => m.ModuleName, StringComparer.Ordinal)
                     .ThenBy(static m => m.TypeFqn, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(sb, ElarionManifest.ModuleKey, ElarionManifest.EncodeModule(module));
        }

        foreach (var hooks in moduleEndpointHooks.OrderBy(static h => h.ModuleName, StringComparer.Ordinal)
                     .ThenBy(static h => h.TypeFqn, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(sb, ElarionManifest.ModuleEndpointsKey, ElarionManifest.EncodeModuleEndpoints(hooks));
        }

        foreach (var endpoint in httpEndpoints.OrderBy(static e => e.Verb, StringComparer.Ordinal)
                     .ThenBy(static e => e.Route, StringComparer.Ordinal)
                     .ThenBy(static e => e.EndpointName, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(sb, ElarionManifest.HttpEndpointKey, ElarionManifest.EncodeHttpEndpoint(endpoint));
        }

        foreach (var method in rpcMethods.OrderBy(static m => m.MethodName, StringComparer.Ordinal)
                     .ThenBy(static m => m.RequestTypeFqn, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(sb, ElarionManifest.RpcMethodKey, ElarionManifest.EncodeRpcMethod(method));
        }

        foreach (var filter in resourceFilters.OrderBy(static f => f.SpecFqn, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(sb, ElarionManifest.ResourceFilterKey, ElarionManifest.EncodeResourceFilter(filter));
        }

        foreach (var permission in permissions)
        {
            AppendAssemblyMetadata(sb, ElarionManifest.PermissionKey, ElarionManifest.EncodePermission(permission));
        }

        foreach (var role in roles)
        {
            AppendAssemblyMetadata(sb, ElarionManifest.RoleKey, ElarionManifest.EncodeRole(role));
        }

        foreach (var variant in variants)
        {
            AppendAssemblyMetadata(sb, ElarionManifest.VariantKey, ElarionManifest.EncodeVariant(variant));
        }

        spc.AddSource("ElarionManifest.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendAssemblyMetadata(StringBuilder sb, string key, string value)
    {
        sb.Append("[assembly: global::System.Reflection.AssemblyMetadataAttribute(");
        sb.Append(SymbolDisplay.FormatLiteral(key, quote: true));
        sb.Append(", ");
        sb.Append(SymbolDisplay.FormatLiteral(value, quote: true));
        sb.AppendLine(")]");
    }

}
