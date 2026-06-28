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
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";
    private const string McpHandlerAttributeMetadataName = "Elarion.Abstractions.McpHandlerAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";

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

        var combined = modules.Combine(httpEndpoints).Combine(rpcMethods);
        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((moduleEntries, httpEndpointEntries), rpcMethodEntries) = source;
            EmitManifest(spc, moduleEntries, httpEndpointEntries, rpcMethodEntries);
        });
    }

    private static ElarionManifest.Module? CreateModule(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

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
                HasStaticMethod(type, "ConfigureEndpointGroup", 1));
        }

        return null;
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
        ImmutableArray<ManifestItem<HttpEndpointEmission.Model>> httpEndpointItems,
        ImmutableArray<ManifestItem<RpcMethodEmission.Model>> rpcMethodItems)
    {
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

        var httpEndpoints = httpEndpointItems
            .Where(static item => item.Model is not null)
            .Select(static item => item.Model!)
            .ToArray();
        var rpcMethods = rpcMethodItems
            .Where(static item => item.Model is not null)
            .Select(static item => item.Model!)
            .ToArray();

        if (modules.Length == 0 && httpEndpoints.Length == 0 && rpcMethods.Length == 0)
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
