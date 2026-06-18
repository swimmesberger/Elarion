using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the implementing half of a <c>RegisterAll</c> partial method by inspecting
/// referenced assemblies for every class annotated with
/// <c>[Elarion.Abstractions.RpcMethodAttribute]</c> and emitting a typed
/// <c>.MapHandler&lt;TRequest, TResponse&gt;(methodName)</c> call per handler. The same pass emits a
/// reflection-free MCP metadata table (<c>McpMetadata()</c>) carrying class/parameter
/// <c>[System.ComponentModel.Description]</c> text and <c>[Elarion.Abstractions.McpMethod]</c> options.
/// </summary>
/// <remarks>
/// Trigger: annotate the partial class with <c>[Elarion.JsonRpc.GenerateRpcMethodMap]</c>.
/// This follows the ServiceScan pattern: <c>ForAttributeWithMetadataName</c> finds the
/// marker in the current project, then <c>.Combine(CompilationProvider)</c> gives access
/// to referenced assemblies.
/// <para>
/// Follows the Roslyn incremental-generator cookbook:
/// <list type="bullet">
///   <item><c>RegisterPostInitializationOutput</c> emits the trigger attribute.</item>
///   <item>Model types are <c>record</c>s with string-only fields — no <c>ISymbol</c>
///         or <c>SyntaxNode</c> stored past the transform step.</item>
///   <item>All pipeline lambdas are <c>static</c>.</item>
///   <item>Code is generated with <see cref="StringBuilder"/>, not SyntaxFactory.</item>
/// </list>
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class RpcMethodMapGenerator : IIncrementalGenerator
{
    // Fully-qualified name FAWMN uses for its index — must match the emitted attribute below.
    private const string TriggerAttributeMetadataName = "Elarion.JsonRpc.GenerateRpcMethodMapAttribute";
    private const string RpcMethodAttributeMetadataName = "Elarion.Abstractions.RpcMethodAttribute";
    private const string McpMethodAttributeMetadataName = "Elarion.Abstractions.McpMethodAttribute";
    private const string DescriptionAttributeMetadataName = "System.ComponentModel.DescriptionAttribute";

    // Build-time warning when two methods collapse to the same MCP tool name. Assumes the default tool-name
    // transform (dots → underscores); a custom ElarionMcpOptions.ToolNameTransform can legitimately differ, hence
    // a warning (and not an error) — the runtime BuildTools check is the authoritative guard.
    private static readonly DiagnosticDescriptor ToolNameCollision = new(
        id: "ELMCP002",
        title: "Duplicate MCP tool name",
        messageFormat:
        "MCP tool name '{0}' is produced by both '{1}' and '{2}' under the default tool-name transform; "
        + "disambiguate via [McpMethod(ToolName = ...)] or a custom ElarionMcpOptions.ToolNameTransform",
        category: "Elarion.Mcp",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Cookbook: record with string-only fields for value equality; no ISymbol/SyntaxNode.
    private sealed record ClassTarget(string? Namespace, string ClassName);

    // Plain-string MCP metadata for one request property (the .NET property name, not the JSON name).
    private sealed record ParameterDescription(string PropertyName, string Description);

    private sealed record RpcHandlerEntry(
        string MethodName,
        string RequestTypeFqn,
        string ResponseTypeFqn,
        string? ToolName,
        bool McpEnabled,
        string? Description,
        IReadOnlyList<ParameterDescription> Parameters
    );

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Trigger attribute is defined in Elarion.JsonRpc.

        // Step 2: find the class decorated with [GenerateRpcMethodMap] in the current project.
        // FAWMN only scans syntax in the current compilation, but that's exactly what we want
        // here: the trigger class (RpcMethodMap) lives in the host project.
        var classProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TriggerAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) =>
                {
                    // Extract plain strings only — no symbols stored past this step.
                    var ns = ctx.TargetSymbol.ContainingNamespace;
                    return new ClassTarget(
                        ns.IsGlobalNamespace ? null : ns.ToDisplayString(),
                        ctx.TargetSymbol.Name);
                });

        // Step 3: combine with the full compilation to access referenced assemblies where
        // the [RpcMethod]-annotated handlers live.
        var combined = classProvider.Combine(context.CompilationProvider);

        // Step 4: generate.  All heavy work (assembly traversal + code gen) happens here.
        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (target, compilation) = source;
            var entries = CollectHandlerEntries(compilation, spc.CancellationToken);
            if (entries.Count == 0)
                return;

            ReportToolNameCollisions(spc, entries);

            var code = BuildSource(target, entries);
            spc.AddSource("RpcMethodMap.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }

    private static List<RpcHandlerEntry> CollectHandlerEntries(
        Compilation compilation,
        CancellationToken ct)
    {
        var attributeType = compilation.GetTypeByMetadataName(RpcMethodAttributeMetadataName);
        if (attributeType is null)
            return [];

        // Optional metadata attributes — may be absent (e.g. a project that doesn't reference the MCP surface).
        var mcpMethodType = compilation.GetTypeByMetadataName(McpMethodAttributeMetadataName);
        var descriptionType = compilation.GetTypeByMetadataName(DescriptionAttributeMetadataName);

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var entries = new List<RpcHandlerEntry>();

        foreach (var reference in compilation.References)
        {
            ct.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            CollectFromNamespace(assembly.GlobalNamespace, attributeType, mcpMethodType, descriptionType, fmt, entries, ct);
        }

        // Sort by method name for deterministic output.
        entries.Sort(static (a, b) =>
            string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal));

        return entries;
    }

    private static void CollectFromNamespace(
        INamespaceSymbol ns,
        INamedTypeSymbol attributeType,
        INamedTypeSymbol? mcpMethodType,
        INamedTypeSymbol? descriptionType,
        SymbolDisplayFormat fmt,
        List<RpcHandlerEntry> entries,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var type in ns.GetTypeMembers())
        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;

            var methodName = attr.ConstructorArguments[0].Value as string;
            if (methodName is null)
                continue;

            INamedTypeSymbol? requestType = null;
            INamedTypeSymbol? responseType = null;

            foreach (var member in type.GetTypeMembers())
            {
                ct.ThrowIfCancellationRequested();

                // Convention: mutations nest Command, queries nest Query.
                if (member.Name is "Command" or "Query")
                    requestType = member;
                else if (member.Name == "Response")
                    responseType = member;
            }

            if (requestType is null || responseType is null)
                continue;

            // MCP metadata (all optional): tool-name override / enabled flag from [McpMethod];
            // tool description from a class-level [Description]; parameter descriptions from request members.
            var (toolName, mcpEnabled) = ReadMcpMethod(type, mcpMethodType);
            var description = GetDescription(type, descriptionType);
            var parameters = CollectParameterDescriptions(requestType, descriptionType);

            // Convert to strings immediately — no ISymbol stored in the model.
            entries.Add(new RpcHandlerEntry(
                methodName,
                requestType.ToDisplayString(fmt),
                responseType.ToDisplayString(fmt),
                toolName,
                mcpEnabled,
                description,
                parameters));
        }

        foreach (var sub in ns.GetNamespaceMembers())
            CollectFromNamespace(sub, attributeType, mcpMethodType, descriptionType, fmt, entries, ct);
    }

    private static void ReportToolNameCollisions(SourceProductionContext spc, List<RpcHandlerEntry> entries)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.McpEnabled)
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

    private static (string? ToolName, bool Enabled) ReadMcpMethod(INamedTypeSymbol type, INamedTypeSymbol? mcpMethodType)
    {
        if (mcpMethodType is null)
            return (null, true);

        foreach (var attr in type.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, mcpMethodType))
                continue;

            string? toolName = null;
            var enabled = true;
            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "ToolName" when named.Value.Value is string name && name.Length > 0:
                        toolName = name;
                        break;
                    case "Enabled" when named.Value.Value is bool value:
                        enabled = value;
                        break;
                }
            }

            return (toolName, enabled);
        }

        return (null, true);
    }

    private static string? GetDescription(ISymbol symbol, INamedTypeSymbol? descriptionType)
    {
        if (descriptionType is null)
            return null;

        foreach (var attr in symbol.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, descriptionType))
                continue;
            if (attr.ConstructorArguments.Length == 0)
                continue;

            return attr.ConstructorArguments[0].Value as string is { Length: > 0 } value ? value : null;
        }

        return null;
    }

    private static IReadOnlyList<ParameterDescription> CollectParameterDescriptions(
        INamedTypeSymbol requestType,
        INamedTypeSymbol? descriptionType)
    {
        if (descriptionType is null)
            return [];

        // Positional records attach a bare [Description] to the constructor parameter; map those by name.
        var byParameterName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ctor in requestType.InstanceConstructors)
        {
            // Skip the synthesized copy constructor (single parameter of the record's own type).
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, requestType))
                continue;

            foreach (var parameter in ctor.Parameters)
            {
                if (GetDescription(parameter, descriptionType) is { } desc)
                    byParameterName[parameter.Name] = desc;
            }
        }

        // Iterate public instance properties in declaration order (deterministic). A property-level
        // [Description] (incl. [property: Description] on positional records) wins over the parameter one.
        var result = new List<ParameterDescription>();
        foreach (var member in requestType.GetMembers())
        {
            if (member is not IPropertySymbol property ||
                property.IsStatic ||
                property.IsIndexer ||
                property.DeclaredAccessibility != Accessibility.Public)
                continue;

            var description = GetDescription(property, descriptionType)
                ?? (byParameterName.TryGetValue(property.Name, out var fromParameter) ? fromParameter : null);

            if (description is not null)
                result.Add(new ParameterDescription(property.Name, description));
        }

        return result;
    }

    private static string BuildSource(ClassTarget target, List<RpcHandlerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.RpcMethodMapGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        // Elarion: MapHandler extension (Elarion.RpcDispatcherExtensions); Elarion.JsonRpc: JsonRpcDispatcher.
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
        sb.AppendLine("    public static partial JsonRpcDispatcher RegisterAll(JsonRpcDispatcher dispatcher)");
        sb.AppendLine("    {");
        sb.AppendLine("        return dispatcher");

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isLast = i == entries.Count - 1;
            sb.Append(
                $"            .MapHandler<{entry.RequestTypeFqn}, {entry.ResponseTypeFqn}>(\"{entry.MethodName}\")");
            sb.AppendLine(isLast ? ";" : string.Empty);
        }

        sb.AppendLine("    }");

        AppendMcpMetadata(sb, entries);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendMcpMetadata(StringBuilder sb, List<RpcHandlerEntry> entries)
    {
        const string Ns = "global::Elarion.JsonRpc.Mcp";

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Reflection-free MCP metadata for the registered methods (see RpcMethodMapGenerator).</summary>");
        sb.AppendLine($"    public static {Ns}.IRpcMcpMetadataSource McpMetadata()");
        sb.AppendLine("    {");
        sb.AppendLine($"        return new {Ns}.RpcMcpMetadataSource(new {Ns}.RpcMcpMethodMetadata[]");
        sb.AppendLine("        {");

        foreach (var entry in entries)
        {
            sb.AppendLine($"            new {Ns}.RpcMcpMethodMetadata");
            sb.AppendLine("            {");
            sb.AppendLine($"                MethodName = {Literal(entry.MethodName)},");
            sb.AppendLine($"                RequestType = typeof({entry.RequestTypeFqn}),");
            sb.AppendLine($"                Enabled = {(entry.McpEnabled ? "true" : "false")},");
            if (entry.ToolName is not null)
                sb.AppendLine($"                ToolName = {Literal(entry.ToolName)},");
            if (entry.Description is not null)
                sb.AppendLine($"                Description = {Literal(entry.Description)},");
            if (entry.Parameters.Count > 0)
            {
                sb.AppendLine($"                Parameters = new {Ns}.RpcMcpParameterDescriptor[]");
                sb.AppendLine("                {");
                foreach (var parameter in entry.Parameters)
                {
                    sb.AppendLine(
                        $"                    new {Ns}.RpcMcpParameterDescriptor({Literal(parameter.PropertyName)}, {Literal(parameter.Description)}),");
                }

                sb.AppendLine("                },");
            }

            sb.AppendLine("            },");
        }

        sb.AppendLine("        });");
        sb.AppendLine("    }");
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
