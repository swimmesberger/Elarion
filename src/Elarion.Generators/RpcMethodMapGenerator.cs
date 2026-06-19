using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the implementing half of a flat <c>RegisterAll</c> partial method by consuming referenced assembly
/// manifests for every class annotated with <c>[Elarion.Abstractions.RpcMethodAttribute]</c> and emitting a typed
/// <c>.MapHandler&lt;TRequest, TResponse&gt;(methodName)</c> call per handler (discovery via
/// <see cref="RpcMethodEmission"/>). The same pass emits a reflection-free MCP metadata table
/// (<c>McpMetadata()</c>) carrying class/parameter descriptions and <c>[McpMethod]</c> options.
/// </summary>
/// <remarks>
/// Trigger: annotate the partial class with <c>[Elarion.JsonRpc.GenerateRpcMethodMap]</c>. This is the
/// <em>non-module</em> path — it registers every discovered method unconditionally. Module-based hosts instead use
/// the feature-flag-gated registration emitted by <see cref="AppModuleDiscoveryGenerator"/>.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class RpcMethodMapGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName = "Elarion.JsonRpc.GenerateRpcMethodMapAttribute";

    // Build-time warning when two methods collapse to the same MCP tool name under the default transform.
    private static readonly DiagnosticDescriptor ToolNameCollision = new(
        id: "ELMCP002",
        title: "Duplicate MCP tool name",
        messageFormat:
        "MCP tool name '{0}' is produced by both '{1}' and '{2}' under the default tool-name transform; "
        + "disambiguate via [McpMethod(ToolName = ...)] or a custom ElarionMcpOptions.ToolNameTransform",
        category: "Elarion.Mcp",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record ClassTarget(string? Namespace, string ClassName);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TriggerAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) =>
                {
                    var ns = ctx.TargetSymbol.ContainingNamespace;
                    return new ClassTarget(
                        ns.IsGlobalNamespace ? null : ns.ToDisplayString(),
                        ctx.TargetSymbol.Name);
                });

        var manifestProvider = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ElarionManifestReader.Read(reference, ct))
            .Collect();

        var combined = classProvider.Combine(manifestProvider).Combine(context.CompilationProvider);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((target, manifests), _) = source;
            var manifest = ElarionManifest.Data.Combine(manifests);
            var entries = manifest.RpcMethods.ToList();
            entries = Deduplicate(entries);
            entries.Sort(static (a, b) => string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal));
            if (entries.Count == 0)
                return;

            ReportToolNameCollisions(spc, entries);

            var code = BuildSource(target, entries);
            spc.AddSource("RpcMethodMap.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }

    private static void ReportToolNameCollisions(SourceProductionContext spc, List<RpcMethodEmission.Model> entries)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.OnMcp)
                continue;

            var toolName = entry.ToolName is { Length: > 0 } overridden
                ? overridden
                : entry.MethodName.Replace(".", "_");

            if (seen.TryGetValue(toolName, out var existing))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    ToolNameCollision, Location.None, toolName, existing, entry.MethodName));
            }
            else
            {
                seen[toolName] = entry.MethodName;
            }
        }
    }

    private static List<RpcMethodEmission.Model> Deduplicate(IEnumerable<RpcMethodEmission.Model> entries)
    {
        var result = new List<RpcMethodEmission.Model>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = string.Join(
                "\u001f",
                entry.MethodName,
                entry.RequestTypeFqn,
                entry.ResponseTypeFqn);
            if (seen.Add(key))
                result.Add(entry);
        }

        return result;
    }

    private static string BuildSource(ClassTarget target, List<RpcMethodEmission.Model> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.RpcMethodMapGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Elarion;");
        sb.AppendLine("using Elarion.JsonRpc;");
        sb.AppendLine();

        if (target.Namespace is not null)
        {
            sb.AppendLine($"namespace {target.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static partial class {target.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Registers every [RpcMethod] handler exposed on the JSON-RPC transport.</summary>");
        sb.AppendLine("    public static partial JsonRpcDispatcher RegisterAll(JsonRpcDispatcher dispatcher)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
        {
            if (!entry.OnJsonRpc)
                continue;

            RpcMethodEmission.AppendMapHandler(sb, entry, "        ", "dispatcher");
        }

        sb.AppendLine("        return dispatcher;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Registers every [RpcMethod] handler exposed on the MCP transport.</summary>");
        sb.AppendLine("    public static JsonRpcDispatcher RegisterMcpAll(JsonRpcDispatcher dispatcher)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
        {
            if (!entry.OnMcp)
                continue;

            RpcMethodEmission.AppendMapHandler(sb, entry, "        ", "dispatcher");
        }

        sb.AppendLine("        return dispatcher;");
        sb.AppendLine("    }");

        AppendMcpMetadata(sb, entries);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendMcpMetadata(StringBuilder sb, List<RpcMethodEmission.Model> entries)
    {
        const string Ns = McpMetadataEmission.Ns;

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Reflection-free MCP metadata for the MCP-exposed methods (see RpcMethodMapGenerator).</summary>");
        sb.AppendLine($"    public static {Ns}.IRpcMcpMetadataSource McpMetadata()");
        sb.AppendLine("    {");
        sb.AppendLine($"        return new {Ns}.RpcMcpMetadataSource(new {Ns}.RpcMcpMethodMetadata[]");
        sb.AppendLine("        {");
        McpMetadataEmission.AppendElements(sb, entries, "            ");
        sb.AppendLine("        });");
        sb.AppendLine("    }");
    }
}
