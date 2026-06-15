using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the implementing half of a <c>RegisterAll</c> partial method by inspecting
/// referenced assemblies for every class annotated with
/// <c>[Elarion.Abstractions.RpcMethodAttribute]</c> and emitting a typed
/// <c>.MapHandler&lt;TRequest, TResponse&gt;(methodName)</c> call per handler.
/// </summary>
/// <remarks>
/// Trigger: annotate the partial class with <c>[Elarion.AspNetCore.GenerateRpcMethodMap]</c>.
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
    private const string TriggerAttributeMetadataName = "Elarion.AspNetCore.GenerateRpcMethodMapAttribute";
    private const string RpcMethodAttributeMetadataName = "Elarion.Abstractions.RpcMethodAttribute";

    // Cookbook: record with string-only fields for value equality; no ISymbol/SyntaxNode.
    private sealed record ClassTarget(string? Namespace, string ClassName);

    private sealed record RpcHandlerEntry(
        string MethodName,
        string RequestTypeFqn,
        string ResponseTypeFqn
    );

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Trigger attribute is defined in Elarion.AspNetCore.

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

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var entries = new List<RpcHandlerEntry>();

        foreach (var reference in compilation.References)
        {
            ct.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            CollectFromNamespace(assembly.GlobalNamespace, attributeType, fmt, entries, ct);
        }

        // Sort by method name for deterministic output.
        entries.Sort(static (a, b) =>
            string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal));

        return entries;
    }

    private static void CollectFromNamespace(
        INamespaceSymbol ns,
        INamedTypeSymbol attributeType,
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

            // Convert to strings immediately — no ISymbol stored in the model.
            entries.Add(new RpcHandlerEntry(
                methodName,
                requestType.ToDisplayString(fmt),
                responseType.ToDisplayString(fmt)));
        }

        foreach (var sub in ns.GetNamespaceMembers())
            CollectFromNamespace(sub, attributeType, fmt, entries, ct);
    }

    private static string BuildSource(ClassTarget target, List<RpcHandlerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.RpcMethodMapGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Elarion.AspNetCore;");
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
        sb.AppendLine("}");

        return sb.ToString();
    }
}
