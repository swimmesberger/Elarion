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
    private const string McpHandlerAttributeMetadataName = "Elarion.Abstractions.McpHandlerAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";
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
                static (ctx, _) => CreateModule(ctx))
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
                static (ctx, _) => CreateResourceFilter(ctx))
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
    // generator via VariantDiscovery, so the manifest and the registry cannot drift.

    private static ElarionManifest.ResourceFilter? CreateResourceFilter(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol specType)
            return null;

        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].AttributeClass is not { TypeArguments.Length: 1 } attributeClass ||
            attributeClass.TypeArguments[0] is not INamedTypeSymbol entity)
        {
            return null;
        }

        var shared = false;
        foreach (var named in ctx.Attributes[0].NamedArguments)
        {
            if (named.Key == "Shared" && named.Value.Value is true)
                shared = true;
        }

        return new ElarionManifest.ResourceFilter(
            specType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            entity.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            specType.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            shared);
    }

    private static ElarionManifest.Module? CreateModule(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var clientFeaturesType =
            ctx.SemanticModel.Compilation.GetTypeByMetadataName(ElarionGeneratorConventions.ClientFeaturesAttribute);

        foreach (var attr in ctx.Attributes)
        {
            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not string moduleName)
                continue;

            string? dependsOn = null;
            var isCore = false;
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "DependsOn" && named.Value.Value is string deps)
                    dependsOn = deps;
                else if (named.Key == "Kind")
                    isCore = IsCoreModuleKind(named.Value);
            }

            return new ElarionManifest.Module(
                moduleName,
                type.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                dependsOn,
                isCore,
                HasStaticMethod(type, "ConfigureServices", 2),
                HasStaticMethod(type, "MapEndpoints", 1),
                HasStaticMethod(type, "GetJsonTypeInfoResolver", 0),
                HasStaticMethod(type, "ConfigureEndpointGroup", 1),
                ReadClientFeatures(type, clientFeaturesType));
        }

        return null;
    }

    /// <summary>Reads the names listed by a module's <c>[ClientFeatures(...)]</c> attribute (empty when absent).</summary>
    private static EquatableArray<string> ReadClientFeatures(INamedTypeSymbol type, INamedTypeSymbol? clientFeaturesType)
    {
        if (clientFeaturesType is null)
            return EquatableArray<string>.Empty;

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, clientFeaturesType))
                continue;
            if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Kind != TypedConstantKind.Array)
                return EquatableArray<string>.Empty;

            var names = new List<string>();
            foreach (var value in attr.ConstructorArguments[0].Values)
            {
                if (value.Value is string name && name.Length > 0)
                    names.Add(name);
            }

            return names.ToEquatableArray();
        }

        return EquatableArray<string>.Empty;
    }

    // A [ModuleEndpoints] contributor: the named module plus which convention hooks the class declares. A class
    // with no usable hook is reported (ELMOD005) and not published — an empty entry could never map anything.
    private static ManifestItem<ElarionManifest.ModuleEndpoints> CreateModuleEndpoints(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return new ManifestItem<ElarionManifest.ModuleEndpoints>(null, ImmutableArray<Diagnostic>.Empty);

        foreach (var attr in ctx.Attributes)
        {
            if (attr.ConstructorArguments.Length == 0 ||
                attr.ConstructorArguments[0].Value is not string moduleName ||
                moduleName.Length == 0)
            {
                continue;
            }

            var hasMapEndpoints = HasStaticMethod(type, "MapEndpoints", 1);
            var hasConfigureEndpointGroup = HasStaticMethod(type, "ConfigureEndpointGroup", 1);
            if (!hasMapEndpoints && !hasConfigureEndpointGroup)
            {
                return new ManifestItem<ElarionManifest.ModuleEndpoints>(
                    null,
                    [
                        Diagnostic.Create(
                            ModuleEndpointsWithoutHooks,
                            type.Locations.FirstOrDefault() ?? Location.None,
                            type.ToDisplayString(),
                            moduleName),
                    ]);
            }

            return new ManifestItem<ElarionManifest.ModuleEndpoints>(
                new ElarionManifest.ModuleEndpoints(
                    moduleName,
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    hasMapEndpoints,
                    hasConfigureEndpointGroup),
                ImmutableArray<Diagnostic>.Empty);
        }

        return new ManifestItem<ElarionManifest.ModuleEndpoints>(null, ImmutableArray<Diagnostic>.Empty);
    }

    private static ManifestItem<HttpEndpointEmission.Model> CreateHttpEndpoint(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return new ManifestItem<HttpEndpointEmission.Model>(null, diagnostics.ToImmutable());

        var descriptionType = ctx.SemanticModel.Compilation.GetTypeByMetadataName(DescriptionAttributeMetadataName);
        foreach (var attr in ctx.Attributes)
        {
            if (HttpEndpointEmission.TryCreateModel(
                    type,
                    attr,
                    descriptionType,
                    SymbolDisplayFormat.FullyQualifiedFormat,
                    diagnostics.Add,
                    ct,
                    out var model) && model is not null)
            {
                return new ManifestItem<HttpEndpointEmission.Model>(model, diagnostics.ToImmutable());
            }
        }

        return new ManifestItem<HttpEndpointEmission.Model>(null, diagnostics.ToImmutable());
    }

    private static ManifestItem<RpcMethodEmission.Model> CreateRpcMethod(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return new ManifestItem<RpcMethodEmission.Model>(null, diagnostics.ToImmutable());

        var compilation = ctx.SemanticModel.Compilation;
        var mcpMethodType = compilation.GetTypeByMetadataName(McpHandlerAttributeMetadataName);
        var descriptionType = compilation.GetTypeByMetadataName(DescriptionAttributeMetadataName);
        foreach (var attr in ctx.Attributes)
        {
            if (RpcMethodEmission.TryCreateModel(
                    type,
                    attr,
                    mcpMethodType,
                    descriptionType,
                    SymbolDisplayFormat.FullyQualifiedFormat,
                    diagnostics.Add,
                    ct,
                    out var model) && model is not null)
            {
                return new ManifestItem<RpcMethodEmission.Model>(model, diagnostics.ToImmutable());
            }
        }

        return new ManifestItem<RpcMethodEmission.Model>(null, diagnostics.ToImmutable());
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

    private static bool HasStaticMethod(INamedTypeSymbol type, string name, int paramCount)
    {
        foreach (var member in type.GetMembers(name))
        {
            if (member is IMethodSymbol { IsStatic: true } method && method.Parameters.Length == paramCount)
                return true;
        }

        return false;
    }

    private static bool IsCoreModuleKind(TypedConstant value)
    {
        if (value.Type is not INamedTypeSymbol enumType)
            return false;

        foreach (var member in enumType.GetMembers("Core"))
        {
            if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, value.Value))
                return true;
        }

        return false;
    }
}
