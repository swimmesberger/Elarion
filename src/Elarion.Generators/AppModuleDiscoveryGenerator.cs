using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates a <c>ModuleBootstrapper</c> class that auto-discovers all classes annotated
/// with <c>[AppModule]</c> and emits feature-flag-gated calls to their convention-based
/// static methods (<c>ConfigureServices</c>, <c>MapEndpoints</c>, <c>GetJsonTypeInfoResolver</c>).
/// <para>
/// Trigger: annotate a partial class with <c>[GenerateModuleBootstrapper]</c> in the host
/// project. The generator scans all referenced assemblies for <c>[AppModule]</c> classes
/// and topologically sorts them by declared dependencies.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AppModuleDiscoveryGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.AspNetCore.GenerateModuleBootstrapperAttribute";

    private const string AppModuleAttributeMetadataName =
        "Elarion.Abstractions.Modules.AppModuleAttribute";

    private sealed record ClassTarget(string? Namespace, string ClassName);

    private sealed record ModuleEntry(
        string ModuleName,
        string TypeFqn,
        string? DependsOn,
        bool IsCore,
        bool HasConfigureServices,
        bool HasMapEndpoints,
        bool HasGetJsonTypeInfoResolver
    );

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Trigger attribute is defined in Elarion.AspNetCore.

        // Step 2: Find the class decorated with [GenerateModuleBootstrapper].
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

        // Step 3: Combine with compilation for assembly scanning.
        var combined = classProvider.Combine(context.CompilationProvider);

        // Step 4: Generate.
        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (target, compilation) = source;
            var entries = CollectModuleEntries(compilation, spc.CancellationToken);

            var sorted = TopologicalSort(entries);
            var code = BuildSource(target, sorted);
            spc.AddSource("ModuleBootstrapper.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }

    private static List<ModuleEntry> CollectModuleEntries(
        Compilation compilation,
        CancellationToken ct)
    {
        var attributeType = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (attributeType is null)
            return new List<ModuleEntry>();

        var entries = new List<ModuleEntry>();

        // Scan the current compilation's assembly first.
        CollectFromNamespace(
            compilation.Assembly.GlobalNamespace,
            attributeType, entries, ct);

        // Then scan referenced assemblies.
        foreach (var reference in compilation.References)
        {
            ct.ThrowIfCancellationRequested();

            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            CollectFromNamespace(assembly.GlobalNamespace, attributeType, entries, ct);
        }

        // Sort by name for deterministic output.
        entries.Sort(static (a, b) =>
            string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal));

        return entries;
    }

    private static void CollectFromNamespace(
        INamespaceSymbol ns,
        INamedTypeSymbol attributeType,
        List<ModuleEntry> entries,
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

            var moduleName = attr.ConstructorArguments[0].Value as string;
            if (moduleName is null)
                continue;

            string? dependsOn = null;
            var isCore = false;
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "DependsOn" && named.Value.Value is string deps)
                {
                    dependsOn = deps;
                }
                else if (named.Key == "Kind")
                {
                    isCore = IsCoreModuleKind(named.Value);
                }
            }

            var hasConfigureServices = HasStaticMethod(type, "ConfigureServices", 2);
            var hasMapEndpoints = HasStaticMethod(type, "MapEndpoints", 1);
            var hasGetJsonTypeInfoResolver = HasStaticMethod(type, "GetJsonTypeInfoResolver", 0);

            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            entries.Add(new ModuleEntry(
                moduleName, fqn, dependsOn,
                isCore,
                hasConfigureServices, hasMapEndpoints, hasGetJsonTypeInfoResolver));
        }

        foreach (var sub in ns.GetNamespaceMembers())
            CollectFromNamespace(sub, attributeType, entries, ct);
    }

    private static bool HasStaticMethod(INamedTypeSymbol type, string name, int paramCount)
    {
        foreach (var member in type.GetMembers(name))
            if (member is IMethodSymbol { IsStatic: true } method
                && method.Parameters.Length == paramCount)
                return true;

        return false;
    }

    private static bool IsCoreModuleKind(TypedConstant value)
    {
        if (value.Type is not INamedTypeSymbol enumType)
            return false;

        foreach (var member in enumType.GetMembers("Core"))
        {
            if (member is IFieldSymbol { HasConstantValue: true } field
                && Equals(field.ConstantValue, value.Value))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Topologically sorts modules by their <c>DependsOn</c> declarations.
    /// Falls back to alphabetical order for modules with no dependencies.
    /// </summary>
    private static List<ModuleEntry> TopologicalSort(List<ModuleEntry> entries)
    {
        if (entries.Count == 0)
            return entries;

        var byName = new Dictionary<string, ModuleEntry>();
        var graph = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var entry in entries)
        {
            byName[entry.ModuleName] = entry;
            if (!graph.ContainsKey(entry.ModuleName))
                graph[entry.ModuleName] = new List<string>();
            if (!inDegree.ContainsKey(entry.ModuleName))
                inDegree[entry.ModuleName] = 0;
        }

        foreach (var entry in entries)
        {
            if (entry.DependsOn is null)
                continue;

            foreach (var dep in entry.DependsOn.Split(','))
            {
                var trimmed = dep.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (!graph.ContainsKey(trimmed))
                    graph[trimmed] = new List<string>();

                graph[trimmed].Add(entry.ModuleName);
                inDegree[entry.ModuleName] = inDegree.TryGetValue(entry.ModuleName, out var d)
                    ? d + 1
                    : 1;
            }
        }

        // Kahn's algorithm. Core modules are prioritized when multiple modules are ready.
        var ready = new List<string>();
        foreach (var kv in inDegree)
            if (kv.Value == 0)
                InsertReadyModule(ready, kv.Key, byName);

        var sorted = new List<ModuleEntry>();
        while (ready.Count > 0)
        {
            var current = ready[0];
            ready.RemoveAt(0);
            if (byName.TryGetValue(current, out var entry))
                sorted.Add(entry);

            if (!graph.TryGetValue(current, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    InsertReadyModule(ready, neighbor, byName);
            }
        }

        // If some entries were not sorted (cycle or unknown deps), append them.
        var sortedNames = new HashSet<string>(sorted.Select(e => e.ModuleName));
        foreach (var entry in entries)
            if (!sortedNames.Contains(entry.ModuleName))
                sorted.Add(entry);

        return sorted;
    }

    private static void InsertReadyModule(
        List<string> ready,
        string moduleName,
        Dictionary<string, ModuleEntry> byName)
    {
        var insertAt = ready.BinarySearch(
            moduleName,
            Comparer<string>.Create((left, right) => CompareReadyModules(left, right, byName)));
        if (insertAt < 0)
            insertAt = ~insertAt;

        ready.Insert(insertAt, moduleName);
    }

    private static int CompareReadyModules(
        string left,
        string right,
        Dictionary<string, ModuleEntry> byName)
    {
        var leftKind = byName.TryGetValue(left, out var leftEntry) && leftEntry.IsCore ? 0 : 1;
        var rightKind = byName.TryGetValue(right, out var rightEntry) && rightEntry.IsCore ? 0 : 1;
        var kindComparison = leftKind.CompareTo(rightKind);
        if (kindComparison != 0)
            return kindComparison;

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string BuildSource(ClassTarget target, List<ModuleEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.AppModuleDiscoveryGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (target.Namespace is not null)
        {
            sb.AppendLine($"namespace {target.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static partial class {target.ClassName}");
        sb.AppendLine("{");

        // --- ConfigureAllServices ---
        sb.AppendLine("    /// <summary>Calls ConfigureServices on all enabled modules.</summary>");
        sb.AppendLine("    public static void ConfigureAllServices(");
        sb.AppendLine("        global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
        {
            if (!entry.HasConfigureServices)
                continue;

            EmitModuleCall(
                sb,
                entry,
                $"{entry.TypeFqn}.ConfigureServices(services, configuration);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- MapAllEndpoints ---
        sb.AppendLine("    /// <summary>Calls MapEndpoints on all enabled modules.</summary>");
        sb.AppendLine("    public static void MapAllEndpoints(");
        sb.AppendLine("        global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
        {
            if (!entry.HasMapEndpoints)
                continue;

            EmitModuleCall(
                sb,
                entry,
                $"{entry.TypeFqn}.MapEndpoints(endpoints);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- GetAllJsonTypeInfoResolvers ---
        sb.AppendLine("    /// <summary>Collects JSON type info resolvers from all enabled modules.</summary>");
        sb.AppendLine(
            "    public static global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver[] GetAllJsonTypeInfoResolvers(");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        sb.AppendLine(
            "        var resolvers = new global::System.Collections.Generic.List<global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver>();");
        foreach (var entry in entries)
        {
            if (!entry.HasGetJsonTypeInfoResolver)
                continue;

            EmitModuleCall(
                sb,
                entry,
                $"resolvers.Add({entry.TypeFqn}.GetJsonTypeInfoResolver());");
        }

        sb.AppendLine("        return resolvers.ToArray();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- IsModuleEnabled ---
        sb.AppendLine("    /// <summary>Returns whether a module is enabled by generated module metadata and configuration.</summary>");
        sb.AppendLine("    public static bool IsModuleEnabled(");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration,");
        sb.AppendLine("        string moduleName)");
        sb.AppendLine("    {");
        sb.AppendLine("        return moduleName switch");
        sb.AppendLine("        {");
        foreach (var entry in entries)
        {
            if (entry.IsCore)
            {
                sb.AppendLine($"            {SourceString(entry.ModuleName)} => true,");
            }
            else
            {
                sb.AppendLine(
                    $"            {SourceString(entry.ModuleName)} => global::Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<bool>(configuration, \"Modules:{entry.ModuleName}:Enabled\", true),");
            }
        }
        sb.AppendLine("            _ => global::Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<bool>(configuration, $\"Modules:{moduleName}:Enabled\", true),");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- GetRegisteredModuleNames ---
        sb.AppendLine("    /// <summary>Returns all discovered module names (for diagnostics).</summary>");
        sb.AppendLine("    public static string[] GetAllModuleNames()");
        sb.AppendLine("    {");
        if (entries.Count == 0)
        {
            sb.AppendLine("        return global::System.Array.Empty<string>();");
        }
        else
        {
            sb.Append("        return new string[] { ");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"\"{entries[i].ModuleName}\"");
            }

            sb.AppendLine(" };");
        }

        sb.AppendLine("    }");

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void EmitModuleCall(StringBuilder sb, ModuleEntry entry, string statement)
    {
        if (entry.IsCore)
        {
            sb.AppendLine($"        {statement}");
            return;
        }

        sb.AppendLine(
            $"        if (IsModuleEnabled(configuration, {SourceString(entry.ModuleName)}))");
        sb.AppendLine($"            {statement}");
    }

    private static string SourceString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
