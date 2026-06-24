using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates a <c>ModuleBootstrapper</c> class that auto-discovers all classes annotated
/// with <c>[AppModule]</c> and emits feature-flag-gated calls to their convention-based
/// static methods (<c>ConfigureServices</c>, <c>MapEndpoints</c>, <c>GetJsonTypeInfoResolver</c>).
/// <para>
/// It additionally groups <c>[HttpEndpoint]</c> and <c>[RpcMethod]</c> handlers by their owning module
/// (longest-prefix namespace match) and emits per-module, per-transport methods — <c>Map{Module}Http</c>,
/// <c>Add{Module}JsonRpc</c>, <c>Add{Module}Mcp</c>, and <c>Get{Module}McpMetadata</c> — plus gated aggregate
/// entry points (<c>MapElarion</c>, <c>RegisterRpcMethods</c>, <c>RegisterMcpMethods</c>,
/// <c>GetMcpMetadata</c>). A handler chooses its dispatcher-based surfaces via
/// <c>[RpcMethod(..., Transports = RpcTransports.JsonRpc | RpcTransports.Mcp)]</c> (both by default), so a method can
/// be JSON-RPC-only, MCP-only, or both. This makes a disabled module disappear across every transport surface, and
/// the per-module methods are emitted as extension methods, so they double as the customization hook (e.g.
/// <c>app.MapGroup("/billing").RequireAuthorization(policy).MapBillingHttp()</c>).
/// Transport-specific emission/discovery is shared with the flat generators via
/// <see cref="HttpEndpointEmission"/>, <see cref="RpcMethodEmission"/>, and <see cref="McpMetadataEmission"/>.
/// </para>
/// <para>
/// Trigger: annotate a partial class with <c>[GenerateModuleBootstrapper]</c> in the host
/// project. The generator consumes per-assembly Elarion manifests from references, directly reads modules in the
/// current compilation, and topologically sorts discovered modules by declared dependencies.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AppModuleDiscoveryGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.AspNetCore.GenerateModuleBootstrapperAttribute";

    private const string AppModuleAttributeMetadataName =
        "Elarion.Abstractions.Modules.AppModuleAttribute";

    private const string UnmatchedModuleName = "<Unmatched>";

    private static readonly DiagnosticDescriptor SharedModuleNamespace = new(
        id: "ELMOD001",
        title: "Multiple app modules share a namespace",
        messageFormat:
        "Modules '{0}' and '{1}' share namespace '{2}'; generated transport handlers in that namespace are "
        + "associated with the alphabetically first matching module",
        category: "Elarion.Modules",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record ClassTarget(string? Namespace, string ClassName);

    private sealed record ModuleEntry(
        string ModuleName,
        string Namespace,
        string TypeFqn,
        string? DependsOn,
        bool IsCore,
        bool HasConfigureServices,
        bool HasMapEndpoints,
        bool HasGetJsonTypeInfoResolver,
        bool HasConfigureEndpointGroup,
        bool EmitDefaultServices
    );

    /// <summary>Grouped transport handlers, keyed by owning module name, plus the unmatched buckets.</summary>
    private sealed record TransportMaps(
        IReadOnlyDictionary<string, List<HttpEndpointEmission.Model>> HttpByModule,
        IReadOnlyList<HttpEndpointEmission.Model> UnmatchedHttp,
        IReadOnlyDictionary<string, List<RpcMethodEmission.Model>> RpcByModule,
        IReadOnlyList<RpcMethodEmission.Model> UnmatchedRpc
    )
    {
        public bool HasHttp => HttpByModule.Count > 0 || UnmatchedHttp.Count > 0;

        public bool HasJsonRpc => HasRpcSurface(static m => m.OnJsonRpc);

        public bool HasMcp => HasRpcSurface(static m => m.OnMcp);

        private bool HasRpcSurface(Func<RpcMethodEmission.Model, bool> predicate)
        {
            foreach (var list in RpcByModule.Values)
            {
                foreach (var method in list)
                {
                    if (predicate(method))
                        return true;
                }
            }

            foreach (var method in UnmatchedRpc)
            {
                if (predicate(method))
                    return true;
            }

            return false;
        }
    }

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
            var ((target, manifests), compilation) = source;
            var manifest = ElarionManifest.Data.Combine(manifests);
            var entries = CollectModuleEntries(
                compilation,
                manifest.Modules,
                spc.ReportDiagnostic,
                spc.CancellationToken);
            var sorted = TopologicalSort(entries);

            var transport = CollectTransportMaps(manifest, entries, spc);

            var code = BuildSource(target, sorted, transport);
            spc.AddSource("ModuleBootstrapper.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }

    private static TransportMaps CollectTransportMaps(
        ElarionManifest.Data manifest,
        List<ModuleEntry> modules,
        SourceProductionContext spc)
    {
        var httpByModule = new Dictionary<string, List<HttpEndpointEmission.Model>>(StringComparer.Ordinal);
        var unmatchedHttp = new List<HttpEndpointEmission.Model>();
        var httpEntries = manifest.HttpEndpoints.ToList();
        httpEntries = DeduplicateHttpEndpoints(httpEntries);
        httpEntries.Sort(static (a, b) =>
        {
            var byVerb = string.Compare(a.Verb, b.Verb, StringComparison.Ordinal);
            if (byVerb != 0)
                return byVerb;
            var byRoute = string.Compare(a.Route, b.Route, StringComparison.Ordinal);
            return byRoute != 0 ? byRoute : string.Compare(a.EndpointName, b.EndpointName, StringComparison.Ordinal);
        });
        HttpEndpointEmission.ReportDuplicateRoutes(httpEntries, spc.ReportDiagnostic);
        foreach (var entry in httpEntries)
        {
            var module = FindBestModule(entry.HandlerNamespace, modules);
            if (module is null)
            {
                unmatchedHttp.Add(entry);
                spc.ReportDiagnostic(Diagnostic.Create(
                    HttpEndpointEmission.UnmatchedModule, Location.None, entry.EndpointName));
                continue;
            }

            Bucket(httpByModule, module.ModuleName).Add(entry);
        }

        var rpcByModule = new Dictionary<string, List<RpcMethodEmission.Model>>(StringComparer.Ordinal);
        var unmatchedRpc = new List<RpcMethodEmission.Model>();
        var rpcEntries = manifest.RpcMethods.ToList();
        rpcEntries = DeduplicateRpcMethods(rpcEntries);
        rpcEntries.Sort(static (a, b) => string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal));
        foreach (var entry in rpcEntries)
        {
            var module = FindBestModule(entry.HandlerNamespace, modules);
            if (module is null)
            {
                unmatchedRpc.Add(entry);
                spc.ReportDiagnostic(Diagnostic.Create(
                    RpcMethodEmission.UnmatchedModule, Location.None, entry.MethodName));
                continue;
            }

            Bucket(rpcByModule, module.ModuleName).Add(entry);
        }

        return new TransportMaps(httpByModule, unmatchedHttp, rpcByModule, unmatchedRpc);
    }

    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    private static ModuleEntry? FindBestModule(string handlerNamespace, List<ModuleEntry> modules)
    {
        ModuleEntry? best = null;
        foreach (var module in modules)
        {
            if (!IsNamespaceInScope(handlerNamespace, module.Namespace))
                continue;
            if (best is null || module.Namespace.Length > best.Namespace.Length)
                best = module;
        }

        return best;
    }

    private static bool IsNamespaceInScope(string handlerNamespace, string moduleNamespace)
    {
        if (moduleNamespace.Length == 0)
            return true;

        return handlerNamespace == moduleNamespace
            || handlerNamespace.StartsWith(moduleNamespace + ".", StringComparison.Ordinal);
    }

    private static List<ModuleEntry> CollectModuleEntries(
        Compilation compilation,
        IReadOnlyList<ElarionManifest.Module> manifestModules,
        Action<Diagnostic> report,
        CancellationToken ct)
    {
        var entries = new List<ModuleEntry>();

        var attributeType = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
        if (attributeType is null)
        {
            foreach (var module in manifestModules)
                entries.Add(ToModuleEntry(module, SiblingExists(compilation, module.TypeFqn)));

            entries = DeduplicateModules(entries);
            entries.Sort(static (a, b) =>
                string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal));
            ReportSharedModuleNamespaces(entries, report);
            return entries;
        }

        // Scan the current compilation's assembly first.
        CollectFromNamespace(
            compilation.Assembly.GlobalNamespace,
            attributeType, entries, ct);

        foreach (var module in manifestModules)
            entries.Add(ToModuleEntry(module, SiblingExists(compilation, module.TypeFqn)));

        entries = DeduplicateModules(entries);

        // Sort by name for deterministic output.
        entries.Sort(static (a, b) =>
            string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal));

        ReportSharedModuleNamespaces(entries, report);

        return entries;
    }

    private static ModuleEntry ToModuleEntry(ElarionManifest.Module module, bool emitDefaultServices) =>
        new(
            module.ModuleName,
            module.Namespace,
            module.TypeFqn,
            module.DependsOn,
            module.IsCore,
            module.HasConfigureServices,
            module.HasMapEndpoints,
            module.HasGetJsonTypeInfoResolver,
            module.HasConfigureEndpointGroup,
            emitDefaultServices);

    /// <summary>
    /// Whether the module's generated <c>ConfigureDefaultServices</c> sibling exists for a referenced module.
    /// Current-compilation modules always emit it (the skeleton runs in the same pass); for referenced modules
    /// the public sibling type is visible in metadata only when that assembly was built with the skeleton generator.
    /// </summary>
    private static bool SiblingExists(Compilation compilation, string typeFqn)
    {
        var metadataName =
            (typeFqn.StartsWith("global::", StringComparison.Ordinal) ? typeFqn.Substring(8) : typeFqn)
            + ModuleDefaultsEmitter.ClassSuffix;
        return compilation.GetTypeByMetadataName(metadataName) is not null;
    }

    private static List<ModuleEntry> DeduplicateModules(IEnumerable<ModuleEntry> entries)
    {
        var result = new List<ModuleEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = string.Join("\u001f", entry.ModuleName, entry.TypeFqn);
            if (seen.Add(key))
                result.Add(entry);
        }

        return result;
    }

    private static List<HttpEndpointEmission.Model> DeduplicateHttpEndpoints(IEnumerable<HttpEndpointEmission.Model> entries)
    {
        var result = new List<HttpEndpointEmission.Model>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = string.Join(
                "\u001f",
                entry.EndpointName,
                entry.RequestTypeFqn,
                entry.ResponseTypeFqn,
                entry.Verb,
                entry.Route);
            if (seen.Add(key))
                result.Add(entry);
        }

        return result;
    }

    private static List<RpcMethodEmission.Model> DeduplicateRpcMethods(IEnumerable<RpcMethodEmission.Model> entries)
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

    private static void ReportSharedModuleNamespaces(IEnumerable<ModuleEntry> entries, Action<Diagnostic> report)
    {
        var byNamespace = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (byNamespace.TryGetValue(entry.Namespace, out var existing))
            {
                report(Diagnostic.Create(SharedModuleNamespace, Location.None, existing, entry.ModuleName, entry.Namespace));
            }
            else
            {
                byNamespace[entry.Namespace] = entry.ModuleName;
            }
        }
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
            var hasConfigureEndpointGroup = HasStaticMethod(type, "ConfigureEndpointGroup", 1);

            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var moduleNamespace = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            entries.Add(new ModuleEntry(
                moduleName, moduleNamespace, fqn, dependsOn,
                isCore,
                hasConfigureServices, hasMapEndpoints, hasGetJsonTypeInfoResolver, hasConfigureEndpointGroup,
                // Current-compilation modules: the ConfigureDefaultServices skeleton is generated in the same pass.
                EmitDefaultServices: true));
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

    private static string BuildSource(ClassTarget target, List<ModuleEntry> entries, TransportMaps transport)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.AppModuleDiscoveryGenerator");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        // Usings are added only when transport mapping is emitted, so hosts without [HttpEndpoint]/[RpcMethod]
        // handlers get the exact same output as before (and no unused-using churn).
        if (transport.HasHttp)
        {
            sb.AppendLine("using Elarion.AspNetCore;");
            sb.AppendLine("using Microsoft.AspNetCore.Builder;");
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
        }

        if (transport.HasJsonRpc || transport.HasMcp)
        {
            sb.AppendLine("using Elarion;");
        }

        if (transport.HasHttp || transport.HasJsonRpc || transport.HasMcp)
            sb.AppendLine();

        if (target.Namespace is not null)
        {
            sb.AppendLine($"namespace {target.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static partial class {target.ClassName}");
        sb.AppendLine("{");

        // --- AddElarion ---
        sb.AppendLine("    /// <summary>Registers default services and calls ConfigureServices on all enabled modules.</summary>");
        sb.AppendLine("    public static void AddElarion(");
        sb.AppendLine("        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
        {
            // Generated defaults (handlers, services, validators, scheduled jobs, event consumers) first,
            // then the module's optional hand-written ConfigureServices for custom registrations. Both gated.
            // The sibling is skipped for referenced modules whose assembly predates the skeleton generator.
            if (entry.EmitDefaultServices)
                EmitModuleCall(
                    sb,
                    entry,
                    $"{entry.TypeFqn}{ModuleDefaultsEmitter.ClassSuffix}.ConfigureDefaultServices(services);");

            if (entry.HasConfigureServices)
                EmitModuleCall(
                    sb,
                    entry,
                    $"{entry.TypeFqn}.ConfigureServices(services, configuration);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- MapElarion (module MapEndpoints + generated [HttpEndpoint] mapping, both gated) ---
        sb.AppendLine("    /// <summary>Calls MapEndpoints and maps generated [HttpEndpoint] handlers on all enabled modules.</summary>");
        sb.AppendLine("    public static void MapElarion(");
        sb.AppendLine("        this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
            EmitModuleEndpointMapping(sb, entry, transport.HttpByModule.ContainsKey(entry.ModuleName));

        if (transport.UnmatchedHttp.Count > 0)
            sb.AppendLine($"        {HttpMethodName(UnmatchedModuleName)}(endpoints);");

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- RegisterRpcMethods (generated [RpcMethod] JSON-RPC registration, gated) — only when JSON-RPC handlers exist ---
        if (transport.HasJsonRpc)
        {
            sb.AppendLine("    /// <summary>Registers JSON-RPC-exposed [RpcMethod] handlers for all enabled modules on the dispatcher.</summary>");
            sb.AppendLine("    public static global::Elarion.JsonRpc.JsonRpcDispatcher RegisterRpcMethods(");
            sb.AppendLine("        this global::Elarion.JsonRpc.JsonRpcDispatcher dispatcher,");
            sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
            sb.AppendLine("    {");
            foreach (var entry in entries)
            {
                if (ModuleHasJsonRpc(transport, entry.ModuleName))
                    EmitModuleCall(sb, entry, $"{JsonRpcMethodName(entry.ModuleName)}(dispatcher);");
            }

            if (AnyJsonRpc(transport.UnmatchedRpc))
                sb.AppendLine($"        {JsonRpcMethodName(UnmatchedModuleName)}(dispatcher);");

            sb.AppendLine("        return dispatcher;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // --- RegisterMcpMethods + GetMcpMetadata (generated MCP surface, gated) — only when MCP handlers exist ---
        if (transport.HasMcp)
        {
            sb.AppendLine("    /// <summary>Registers MCP-exposed [RpcMethod] handlers for all enabled modules on the MCP dispatcher.</summary>");
            sb.AppendLine("    public static global::Elarion.JsonRpc.JsonRpcDispatcher RegisterMcpMethods(");
            sb.AppendLine("        this global::Elarion.JsonRpc.JsonRpcDispatcher dispatcher,");
            sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
            sb.AppendLine("    {");
            foreach (var entry in entries)
            {
                if (ModuleHasMcp(transport, entry.ModuleName))
                    EmitModuleCall(sb, entry, $"{McpMethodName(entry.ModuleName)}(dispatcher);");
            }

            if (McpMetadataEmission.AnyMcp(transport.UnmatchedRpc))
                sb.AppendLine($"        {McpMethodName(UnmatchedModuleName)}(dispatcher);");

            sb.AppendLine("        return dispatcher;");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    /// <summary>Collects MCP tool metadata for all enabled modules.</summary>");
            sb.AppendLine($"    public static {McpMetadataEmission.Ns}.IRpcMcpMetadataSource GetMcpMetadata(");
            sb.AppendLine("        this global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
            sb.AppendLine("    {");
            sb.AppendLine(
                $"        var methods = new global::System.Collections.Generic.List<{McpMetadataEmission.Ns}.RpcMcpMethodMetadata>();");
            foreach (var entry in entries)
            {
                if (ModuleHasMcp(transport, entry.ModuleName))
                    EmitModuleCall(sb, entry, $"methods.AddRange({McpMetadataMethodName(entry.ModuleName)}());");
            }

            if (McpMetadataEmission.AnyMcp(transport.UnmatchedRpc))
                sb.AppendLine($"        methods.AddRange({McpMetadataMethodName(UnmatchedModuleName)}());");

            sb.AppendLine($"        return new {McpMetadataEmission.Ns}.RpcMcpMetadataSource(methods);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // --- GetAllJsonTypeInfoResolvers ---
        sb.AppendLine("    /// <summary>Collects JSON type info resolvers from all enabled modules.</summary>");
        sb.AppendLine(
            "    public static global::System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver[] GetAllJsonTypeInfoResolvers(");
        sb.AppendLine("        this global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
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
        sb.AppendLine("        this global::Microsoft.Extensions.Configuration.IConfiguration configuration,");
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

        // --- Per-module transport methods (group hooks) ---
        AppendHttpMethods(sb, entries, transport);
        AppendRpcMethods(sb, entries, transport);
        AppendMcpMethods(sb, entries, transport);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendHttpMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (!transport.HttpByModule.TryGetValue(entry.ModuleName, out var endpoints))
                continue;

            AppendHttpMethod(sb, entry.ModuleName, endpoints);
        }

        if (transport.UnmatchedHttp.Count > 0)
            AppendHttpMethod(sb, UnmatchedModuleName, transport.UnmatchedHttp);
    }

    private static void AppendHttpMethod(StringBuilder sb, string moduleName, IReadOnlyList<HttpEndpointEmission.Model> endpoints)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Maps the [HttpEndpoint] handlers in the '{moduleName}' module onto <paramref name=\"app\"/>.</summary>");
        sb.AppendLine($"    public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder {HttpMethodName(moduleName)}(");
        sb.AppendLine("        this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)");
        sb.AppendLine("    {");
        foreach (var endpoint in endpoints)
            HttpEndpointEmission.AppendRegistration(sb, endpoint, "        ", "app");
        sb.AppendLine("        return app;");
        sb.AppendLine("    }");
    }

    private static void AppendRpcMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (ModuleHasJsonRpc(transport, entry.ModuleName))
                AppendRpcMethod(sb, entry.ModuleName, transport.RpcByModule[entry.ModuleName]);
        }

        if (AnyJsonRpc(transport.UnmatchedRpc))
            AppendRpcMethod(sb, UnmatchedModuleName, transport.UnmatchedRpc);
    }

    private static void AppendRpcMethod(StringBuilder sb, string moduleName, IReadOnlyList<RpcMethodEmission.Model> methods)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Registers the JSON-RPC-exposed [RpcMethod] handlers in the '{moduleName}' module on <paramref name=\"dispatcher\"/>.</summary>");
        sb.AppendLine($"    public static global::Elarion.JsonRpc.JsonRpcDispatcher {JsonRpcMethodName(moduleName)}(");
        sb.AppendLine("        this global::Elarion.JsonRpc.JsonRpcDispatcher dispatcher)");
        sb.AppendLine("    {");
        foreach (var method in methods)
        {
            if (method.OnJsonRpc)
                RpcMethodEmission.AppendMapHandler(sb, method, "        ", "dispatcher");
        }

        sb.AppendLine("        return dispatcher;");
        sb.AppendLine("    }");
    }

    private static void AppendMcpMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (!ModuleHasMcp(transport, entry.ModuleName))
                continue;

            var methods = transport.RpcByModule[entry.ModuleName];
            AppendMcpMethod(sb, entry.ModuleName, methods);
            AppendMcpMetadataMethod(sb, entry.ModuleName, methods);
        }

        if (McpMetadataEmission.AnyMcp(transport.UnmatchedRpc))
        {
            AppendMcpMethod(sb, UnmatchedModuleName, transport.UnmatchedRpc);
            AppendMcpMetadataMethod(sb, UnmatchedModuleName, transport.UnmatchedRpc);
        }
    }

    private static void AppendMcpMethod(StringBuilder sb, string moduleName, IReadOnlyList<RpcMethodEmission.Model> methods)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Registers the MCP-exposed [RpcMethod] handlers in the '{moduleName}' module on the MCP <paramref name=\"dispatcher\"/>.</summary>");
        sb.AppendLine($"    public static global::Elarion.JsonRpc.JsonRpcDispatcher {McpMethodName(moduleName)}(");
        sb.AppendLine("        this global::Elarion.JsonRpc.JsonRpcDispatcher dispatcher)");
        sb.AppendLine("    {");
        foreach (var method in methods)
        {
            if (method.OnMcp)
                RpcMethodEmission.AppendMapHandler(sb, method, "        ", "dispatcher");
        }

        sb.AppendLine("        return dispatcher;");
        sb.AppendLine("    }");
    }

    private static void AppendMcpMetadataMethod(StringBuilder sb, string moduleName, IReadOnlyList<RpcMethodEmission.Model> methods)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Returns the MCP tool metadata for the '{moduleName}' module.</summary>");
        sb.AppendLine($"    public static {McpMetadataEmission.Ns}.RpcMcpMethodMetadata[] {McpMetadataMethodName(moduleName)}()");
        sb.AppendLine("    {");
        sb.AppendLine($"        return new {McpMetadataEmission.Ns}.RpcMcpMethodMetadata[]");
        sb.AppendLine("        {");
        McpMetadataEmission.AppendElements(sb, methods, "            ");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static bool ModuleHasJsonRpc(TransportMaps transport, string moduleName) =>
        transport.RpcByModule.TryGetValue(moduleName, out var methods) && AnyJsonRpc(methods);

    private static bool ModuleHasMcp(TransportMaps transport, string moduleName) =>
        transport.RpcByModule.TryGetValue(moduleName, out var methods) && McpMetadataEmission.AnyMcp(methods);

    private static bool AnyJsonRpc(IEnumerable<RpcMethodEmission.Model> methods)
    {
        foreach (var method in methods)
        {
            if (method.OnJsonRpc)
                return true;
        }

        return false;
    }

    private static string HttpMethodName(string moduleName) => $"Map{ModuleMethodNamePart(moduleName)}Http";

    private static string JsonRpcMethodName(string moduleName) => $"Add{ModuleMethodNamePart(moduleName)}JsonRpc";

    private static string McpMethodName(string moduleName) => $"Add{ModuleMethodNamePart(moduleName)}Mcp";

    private static string McpMetadataMethodName(string moduleName) => $"Get{ModuleMethodNamePart(moduleName)}McpMetadata";

    private static string ModuleMethodNamePart(string moduleName)
    {
        var sb = new StringBuilder(moduleName.Length);
        var changed = moduleName.Length == 0;
        foreach (var ch in moduleName)
        {
            if (SyntaxFacts.IsIdentifierPartCharacter(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
                changed = true;
            }
        }

        if (!changed)
            return moduleName;

        return $"{sb}_{StableHash(moduleName)}";
    }

    private static string StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash.ToString("X8");
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

    /// <summary>
    /// Emits a module's endpoint mapping (its optional <c>MapEndpoints</c> hook and its generated
    /// <c>[HttpEndpoint]</c> routes) inside the module's feature gate. When the module declares a
    /// <c>ConfigureEndpointGroup</c> hook, both are mapped onto the builder it returns, so the module owns
    /// its own route group/policy/conventions; otherwise they map onto the root <c>endpoints</c> builder.
    /// </summary>
    private static void EmitModuleEndpointMapping(StringBuilder sb, ModuleEntry entry, bool hasHttp)
    {
        if (!entry.HasMapEndpoints && !hasHttp)
            return;

        // The group hook is only meaningful when the module actually maps something.
        var useGroup = entry.HasConfigureEndpointGroup;
        var target = "endpoints";
        var statements = new List<string>();
        if (useGroup)
        {
            target = $"{ModuleMethodNamePart(entry.ModuleName)}Endpoints";
            statements.Add($"var {target} = {entry.TypeFqn}.ConfigureEndpointGroup(endpoints);");
        }

        if (entry.HasMapEndpoints)
            statements.Add($"{entry.TypeFqn}.MapEndpoints({target});");
        if (hasHttp)
            statements.Add($"{HttpMethodName(entry.ModuleName)}({target});");

        if (entry.IsCore)
        {
            // Core modules with a group hook need a block to scope the local; otherwise emit flat.
            if (useGroup)
            {
                sb.AppendLine("        {");
                foreach (var statement in statements)
                    sb.AppendLine($"            {statement}");
                sb.AppendLine("        }");
            }
            else
            {
                foreach (var statement in statements)
                    sb.AppendLine($"        {statement}");
            }

            return;
        }

        sb.AppendLine($"        if (IsModuleEnabled(configuration, {SourceString(entry.ModuleName)}))");
        sb.AppendLine("        {");
        foreach (var statement in statements)
            sb.AppendLine($"            {statement}");
        sb.AppendLine("        }");
    }

    private static string SourceString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
