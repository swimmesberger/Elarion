using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the fixed-name <c>ElarionBootstrapper</c> class that auto-discovers all classes annotated
/// with <c>[AppModule]</c> and emits feature-flag-gated calls to their convention-based
/// static methods (<c>ConfigureServices</c>, <c>MapEndpoints</c>, <c>GetJsonTypeInfoResolver</c>).
/// <para>
/// It additionally groups <c>[HttpEndpoint]</c> and <c>[Handler]</c> handlers by their owning module
/// (longest-prefix namespace match) and emits per-module methods — <c>Map{Module}Http</c>,
/// <c>Add{Module}Handlers</c>, and <c>Get{Module}McpMetadata</c> — plus gated aggregate entry points
/// (<c>MapElarionEndpoints</c>, <c>RegisterHandlers</c>, <c>GetMcpMetadata</c>). <c>RegisterHandlers</c> builds
/// the single transport-neutral <c>HandlerDispatcher</c> (the named bus); each operation carries its
/// <c>HandlerTransports</c> flags, so the JSON-RPC and MCP adapters each expose only the subset they serve. A
/// handler chooses its surfaces via <c>[Handler(..., Transports = HandlerTransports.JsonRpc | HandlerTransports.Mcp)]</c>
/// (both by default), so an operation can be JSON-RPC-only, MCP-only, or both. This makes a disabled module
/// disappear across every transport surface, and
/// the per-module methods are emitted as extension methods, so they double as the customization hook (e.g.
/// <c>app.MapGroup("/billing").RequireAuthorization(policy).MapBillingHttp()</c>).
/// Transport-specific emission/discovery is shared with the flat generators via
/// <see cref="HttpEndpointEmission"/>, <see cref="RpcMethodEmission"/>, and <see cref="McpMetadataEmission"/>.
/// </para>
/// <para>
/// Trigger: <c>[assembly: GenerateModuleBootstrapper]</c> in the host project. The generator emits the
/// fixed-name <c>ElarionBootstrapper</c> static into the host's root namespace (ADR-0018), consumes per-assembly
/// Elarion manifests from references, directly reads modules in the current compilation, and topologically sorts
/// discovered modules by declared dependencies.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AppModuleDiscoveryGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.AspNetCore.GenerateModuleBootstrapperAttribute";

    private const string AppModuleAttributeMetadataName = ElarionGeneratorConventions.AppModuleAttribute;

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
        IReadOnlyList<RpcMethodEmission.Model> UnmatchedRpc,
        IReadOnlyDictionary<string, List<ElarionManifest.ResourceFilter>> ResourceFiltersByModule,
        IReadOnlyList<ElarionManifest.ResourceFilter> UnmatchedResourceFilters
    )
    {
        public bool HasHttp => HttpByModule.Count > 0 || UnmatchedHttp.Count > 0;

        public bool HasResourceFilters => ResourceFiltersByModule.Count > 0 || UnmatchedResourceFilters.Count > 0;

        public bool HasJsonRpc => HasRpcSurface(static m => m.OnJsonRpc);

        public bool HasMcp => HasRpcSurface(static m => m.OnMcp);

        /// <summary>Any <c>[Handler]</c> at all (every handler is on at least one transport) — drives the shared registry.</summary>
        public bool HasHandlers => RpcByModule.Count > 0 || UnmatchedRpc.Count > 0;

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

    private const string BootstrapperTypeName = "ElarionBootstrapper";

    /// <summary>The fully-built bootstrapper output: the final source text plus its diagnostics, both
    /// value-equatable, so the source-output stage (and the re-parse of the emitted file) is skipped whenever
    /// no input actually changed.</summary>
    private sealed record BootstrapperOutput(string? Source, EquatableArray<DiagnosticInfo> Diagnostics);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var manifestProvider = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ElarionManifestReader.Read(reference, ct))
            .Collect();

        // The generated bootstrapper is framework-named (ElarionBootstrapper) and emitted into the host's root
        // namespace, so every Elarion host exposes the same composition root (ADR-0018). Triggered by the
        // assembly attribute, not a user-declared partial.
        var rootNamespace = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? string.Empty)
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) ? ns : null))
            .Select(static (pair, _) => string.IsNullOrEmpty(pair.Right) ? pair.Left : pair.Right!);

        // Current-compilation [AppModule] declarations, discovered per node (cached per tree, ADR-0006) instead
        // of a whole-assembly symbol walk on every keystroke. Top-level types only, matching the old walk over
        // namespace type members.
        var currentModules = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AppModuleAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => CreateModuleEntry(ctx))
            .Where(static entry => entry is not null)
            .Select(static (entry, _) => entry!)
            .Collect()
            .Select(static (entries, _) => entries
                .OrderBy(static e => e.TypeFqn, StringComparer.Ordinal)
                .ToEquatableArray())
            .WithTrackingName("BootstrapperModules");

        // Deliberately compilation-fresh, cheap per-keystroke probes (symbol-table lookups only): the assembly
        // trigger, and which referenced modules' generated ConfigureDefaultServices siblings exist. Both project
        // to small equatable values, so downstream stays cached when they don't change.
        var trigger = context.CompilationProvider
            .Select(static (compilation, _) => HasBootstrapperTrigger(compilation));
        var referencedSiblings = manifestProvider
            .Combine(context.CompilationProvider)
            .Select(static (source, _) => ProbeReferencedSiblings(source.Left, source.Right))
            .WithTrackingName("BootstrapperSiblings");

        var model = manifestProvider
            .Combine(currentModules)
            .Combine(referencedSiblings)
            .Combine(trigger)
            .Combine(rootNamespace)
            .Select(static (source, ct) =>
            {
                var ((((manifests, modules), siblings), hasTrigger), rootNs) = source;
                return BuildBootstrapperOutput(manifests, modules, siblings, hasTrigger, rootNs, ct);
            })
            .WithTrackingName("Bootstrapper");

        context.RegisterSourceOutput(model, static (spc, output) =>
        {
            foreach (var diagnostic in output.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (output.Source is not null)
                spc.AddSource("ElarionBootstrapper.g.cs", SourceText.From(output.Source, Encoding.UTF8));
        });
    }

    private static BootstrapperOutput BuildBootstrapperOutput(
        ImmutableArray<ManifestReadResult> manifests,
        EquatableArray<ModuleEntry> currentModules,
        EquatableArray<string> referencedSiblings,
        bool hasTrigger,
        string rootNs,
        CancellationToken ct)
    {
        if (!hasTrigger)
            return new BootstrapperOutput(null, EquatableArray<DiagnosticInfo>.Empty);

        var diagnostics = new List<DiagnosticInfo>();
        foreach (var result in manifests)
        {
            if (result.Diagnostic is { } manifestDiagnostic)
                diagnostics.Add(manifestDiagnostic);
        }

        var manifest = ElarionManifest.Data.Combine(manifests.Select(static r => r.Data));
        var siblingSet = new HashSet<string>(referencedSiblings.AsImmutableArray, StringComparer.Ordinal);
        var entries = CollectModuleEntries(currentModules, manifest.Modules, siblingSet, diagnostics);
        var sorted = TopologicalSort(entries);
        ct.ThrowIfCancellationRequested();

        var transport = CollectTransportMaps(manifest, entries, diagnostics);

        var target = new ClassTarget(rootNs.Length == 0 ? null : rootNs, BootstrapperTypeName);
        var code = BuildSource(target, sorted, transport);
        return new BootstrapperOutput(code, diagnostics.ToEquatableArray());
    }

    private static EquatableArray<string> ProbeReferencedSiblings(
        ImmutableArray<ManifestReadResult> manifests,
        Compilation compilation)
    {
        var existing = new List<string>();
        foreach (var result in manifests)
        {
            foreach (var module in result.Data.Modules)
            {
                if (SiblingExists(compilation, module.TypeFqn))
                    existing.Add(module.TypeFqn);
            }
        }

        return existing
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static fqn => fqn, StringComparer.Ordinal)
            .ToEquatableArray();
    }

    private static bool HasBootstrapperTrigger(Compilation compilation)
    {
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == TriggerAttributeMetadataName)
                return true;
        }

        return false;
    }

    private static TransportMaps CollectTransportMaps(
        ElarionManifest.Data manifest,
        List<ModuleEntry> modules,
        List<DiagnosticInfo> diagnostics)
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
        HttpEndpointEmission.ReportDuplicateRoutes(httpEntries, diagnostics);
        foreach (var entry in httpEntries)
        {
            var module = FindBestModule(entry.HandlerNamespace, modules);
            if (module is null)
            {
                unmatchedHttp.Add(entry);
                diagnostics.Add(DiagnosticInfo.Create(
                    HttpEndpointEmission.UnmatchedModule, (Location?)null, entry.EndpointName));
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
            // Resolve an inferred operation into its final, module-qualified name now that the owning module is
            // known, so the registry route and the MCP metadata table agree on one name.
            var resolved = ResolveOperationName(entry, module?.ModuleName);
            if (module is null)
            {
                unmatchedRpc.Add(resolved);
                diagnostics.Add(DiagnosticInfo.Create(
                    RpcMethodEmission.UnmatchedModule, (Location?)null, resolved.MethodName));
                continue;
            }

            Bucket(rpcByModule, module.ModuleName).Add(resolved);
        }

        // Final names may differ from the pre-sort operation names; re-sort each bucket for deterministic output.
        foreach (var list in rpcByModule.Values)
            list.Sort(RpcMethodOrder);
        unmatchedRpc.Sort(RpcMethodOrder);

        // Operation names key the single shared bus, so a collision (across modules, or an inferred name clashing
        // with an explicit one) would silently drop a handler at runtime — report it at compile time instead.
        ReportDuplicateOperationNames(rpcByModule, unmatchedRpc, diagnostics);

        var resourceFiltersByModule = new Dictionary<string, List<ElarionManifest.ResourceFilter>>(StringComparer.Ordinal);
        var unmatchedResourceFilters = new List<ElarionManifest.ResourceFilter>();
        var resourceFilterEntries = manifest.ResourceFilters.ToList();
        resourceFilterEntries.Sort(static (a, b) => string.Compare(a.SpecFqn, b.SpecFqn, StringComparison.Ordinal));
        foreach (var entry in resourceFilterEntries)
        {
            var module = FindBestModule(entry.Namespace, modules);
            // A filter under no module is registered ungated rather than silently dropped (mirrors transports).
            if (module is null)
                unmatchedResourceFilters.Add(entry);
            else
                Bucket(resourceFiltersByModule, module.ModuleName).Add(entry);
        }

        return new TransportMaps(
            httpByModule, unmatchedHttp, rpcByModule, unmatchedRpc, resourceFiltersByModule, unmatchedResourceFilters);
    }

    private static void ReportDuplicateOperationNames(
        Dictionary<string, List<RpcMethodEmission.Model>> rpcByModule,
        List<RpcMethodEmission.Model> unmatchedRpc,
        List<DiagnosticInfo> diagnostics)
    {
        // The bus keys names case-insensitively, so detect collisions the same way.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Scan(List<RpcMethodEmission.Model> entries)
        {
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.MethodName) && reported.Add(entry.MethodName))
                    diagnostics.Add(DiagnosticInfo.Create(
                        RpcMethodEmission.DuplicateOperationName, (Location?)null, entry.MethodName));
            }
        }

        foreach (var list in rpcByModule.Values)
            Scan(list);
        Scan(unmatchedRpc);
    }

    private static int RpcMethodOrder(RpcMethodEmission.Model a, RpcMethodEmission.Model b)
    {
        var byName = string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal);
        return byName != 0 ? byName : string.Compare(a.RequestTypeFqn, b.RequestTypeFqn, StringComparison.Ordinal);
    }

    // An inferred name becomes "{module}.{operation}" (module camel-cased); an explicit name is used verbatim.
    // Unmatched inferred handlers keep the bare operation (no module to qualify with).
    private static RpcMethodEmission.Model ResolveOperationName(RpcMethodEmission.Model entry, string? moduleName)
    {
        if (!entry.IsNameInferred)
            return entry;

        var name = moduleName is null
            ? entry.MethodName
            : $"{CamelCaseModule(moduleName)}.{entry.MethodName}";
        return entry with { MethodName = name, IsNameInferred = false };
    }

    private static string CamelCaseModule(string moduleName) =>
        moduleName.Length > 0 && char.IsUpper(moduleName[0])
            ? char.ToLowerInvariant(moduleName[0]) + moduleName.Substring(1)
            : moduleName;

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
        EquatableArray<ModuleEntry> currentModules,
        IReadOnlyList<ElarionManifest.Module> manifestModules,
        HashSet<string> referencedSiblings,
        List<DiagnosticInfo> diagnostics)
    {
        // Current-compilation modules first: when the same module appears in the manifest too, deduplication
        // keeps the first entry, so the current-compilation one (EmitDefaultServices: true) wins.
        var entries = new List<ModuleEntry>();
        foreach (var entry in currentModules)
            entries.Add(entry);

        foreach (var module in manifestModules)
            entries.Add(ToModuleEntry(module, referencedSiblings.Contains(module.TypeFqn)));

        entries = DeduplicateModules(entries);

        // Sort by name for deterministic output.
        entries.Sort(static (a, b) =>
            string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal));

        ReportSharedModuleNamespaces(entries, diagnostics);

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

    private static void ReportSharedModuleNamespaces(IEnumerable<ModuleEntry> entries, List<DiagnosticInfo> diagnostics)
    {
        var byNamespace = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (byNamespace.TryGetValue(entry.Namespace, out var existing))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SharedModuleNamespace, (Location?)null, existing, entry.ModuleName, entry.Namespace));
            }
            else
            {
                byNamespace[entry.Namespace] = entry.ModuleName;
            }
        }
    }

    // The per-node [AppModule] transform (cached per tree). Top-level types only, matching the old walk over
    // namespace type members; everything read here — the attribute arguments and the convention-hook probes —
    // lives on the module type itself, so a stale cache entry is impossible without editing the module's file.
    private static ModuleEntry? CreateModuleEntry(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || type.ContainingType is not null)
            return null;

        foreach (var attr in ctx.Attributes)
        {
            if (attr.ConstructorArguments.Length == 0 ||
                attr.ConstructorArguments[0].Value is not string moduleName)
            {
                continue;
            }

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

            return new ModuleEntry(
                moduleName, moduleNamespace, fqn, dependsOn,
                isCore,
                hasConfigureServices, hasMapEndpoints, hasGetJsonTypeInfoResolver, hasConfigureEndpointGroup,
                // Current-compilation modules: the ConfigureDefaultServices skeleton is generated in the same pass.
                EmitDefaultServices: true);
        }

        return null;
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

        // Usings are added only for HTTP mapping, so hosts without [HttpEndpoint] handlers get the exact same
        // output as before. The handler registry and MCP metadata are emitted fully-qualified (no using needed).
        if (transport.HasHttp)
        {
            sb.AppendLine("using Elarion.AspNetCore;");
            sb.AppendLine("using Microsoft.AspNetCore.Builder;");
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
            sb.AppendLine();
        }

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
        // The typed in-process mediator send, available wherever handlers are. Idempotent (TryAddScoped).
        sb.AppendLine("        global::Elarion.HandlerSenderServiceCollectionExtensions.AddElarionHandlerSender(services);");
        // Contribute every enabled module's source-generated JSON context to the canonical serializer options, so
        // every subsystem (JSON-RPC, MCP, idempotency, caching, outbox, settings) reads one shared configuration.
        sb.AppendLine("        global::Elarion.Abstractions.Serialization.ElarionJsonServiceCollectionExtensions.ConfigureElarionJson(services, o =>");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var resolver in GetAllJsonTypeInfoResolvers(configuration))");
        sb.AppendLine("                o.TypeInfoResolvers.Add(resolver);");
        sb.AppendLine("        });");
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

        // Data-level authorization filters ([ResourceFilter]) are registered as IQueryAuthorizer<T>, gated per module.
        // Discovered from each assembly's Elarion manifest, so a referenced module library's filters register here too.
        if (transport.HasResourceFilters)
        {
            foreach (var entry in entries)
            {
                if (transport.ResourceFiltersByModule.ContainsKey(entry.ModuleName))
                    EmitModuleCall(sb, entry, $"{ResourceFiltersMethodName(entry.ModuleName)}(services);");
            }

            if (transport.UnmatchedResourceFilters.Count > 0)
                sb.AppendLine($"        {ResourceFiltersMethodName(UnmatchedModuleName)}(services);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- MapElarionEndpoints (module MapEndpoints + generated [HttpEndpoint] mapping, both gated) ---
        sb.AppendLine("    /// <summary>Calls MapEndpoints and maps generated [HttpEndpoint] handlers on all enabled modules.</summary>");
        sb.AppendLine("    public static void MapElarionEndpoints(");
        sb.AppendLine("        this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,");
        sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        sb.AppendLine("    {");
        foreach (var entry in entries)
            EmitModuleEndpointMapping(sb, entry, transport.HttpByModule.ContainsKey(entry.ModuleName));

        if (transport.UnmatchedHttp.Count > 0)
            sb.AppendLine($"        {HttpMethodName(UnmatchedModuleName)}(endpoints);");

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- RegisterHandlers (generated [Handler] registration onto the neutral bus, gated) — only when handlers exist ---
        if (transport.HasHandlers)
        {
            sb.AppendLine("    /// <summary>Registers all [Handler] operations for all enabled modules onto the shared handler dispatcher (the named bus).</summary>");
            sb.AppendLine("    public static global::Elarion.Abstractions.Dispatch.HandlerDispatcher RegisterHandlers(");
            sb.AppendLine("        this global::Elarion.Abstractions.Dispatch.HandlerDispatcher dispatcher,");
            sb.AppendLine("        global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
            sb.AppendLine("    {");
            foreach (var entry in entries)
            {
                if (ModuleHasHandlers(transport, entry.ModuleName))
                    EmitModuleCall(sb, entry, $"{HandlersMethodName(entry.ModuleName)}(dispatcher);");
            }

            if (transport.UnmatchedRpc.Count > 0)
                sb.AppendLine($"        {HandlersMethodName(UnmatchedModuleName)}(dispatcher);");

            sb.AppendLine("        return dispatcher;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // --- GetMcpMetadata (generated MCP tool table, gated) — only when MCP handlers exist ---
        if (transport.HasMcp)
        {
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
        AppendHandlersMethods(sb, entries, transport);
        AppendMcpMetadataMethods(sb, entries, transport);
        AppendResourceFilterMethods(sb, entries, transport);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendResourceFilterMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (transport.ResourceFiltersByModule.TryGetValue(entry.ModuleName, out var filters) && filters.Count > 0)
                AppendResourceFilterMethod(sb, entry.ModuleName, filters);
        }

        if (transport.UnmatchedResourceFilters.Count > 0)
            AppendResourceFilterMethod(sb, UnmatchedModuleName, transport.UnmatchedResourceFilters);
    }

    private static void AppendResourceFilterMethod(
        StringBuilder sb, string moduleName, IReadOnlyList<ElarionManifest.ResourceFilter> filters)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Registers the [ResourceFilter] data-level authorizers in the '{moduleName}' module as IQueryAuthorizer&lt;T&gt;.</summary>");
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {ResourceFiltersMethodName(moduleName)}(");
        sb.AppendLine("        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        foreach (var filter in filters)
        {
            var serviceType = $"{ElarionGeneratorConventions.QueryAuthorizerTypeFqn}<{filter.EntityFqn}>";
            if (filter.IsShared)
            {
                // A shared filter consults the grants set (an EXISTS), so it is a scoped service.
                sb.AppendLine(
                    $"        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<{serviceType}, {filter.SpecFqn}>(services);");
            }
            else
            {
                // A field-only filter is a stateless singleton exposed as the static Specification.
                sb.AppendLine(
                    $"        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<{serviceType}>(services, {filter.SpecFqn}.{ElarionGeneratorConventions.ResourceFilterSpecificationMember});");
            }
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
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

    private static void AppendHandlersMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (ModuleHasHandlers(transport, entry.ModuleName))
                AppendHandlersMethod(sb, entry.ModuleName, transport.RpcByModule[entry.ModuleName]);
        }

        if (transport.UnmatchedRpc.Count > 0)
            AppendHandlersMethod(sb, UnmatchedModuleName, transport.UnmatchedRpc);
    }

    private static void AppendHandlersMethod(StringBuilder sb, string moduleName, IReadOnlyList<RpcMethodEmission.Model> methods)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Registers the [Handler] operations in the '{moduleName}' module onto <paramref name=\"dispatcher\"/> (with each operation's transport flags).</summary>");
        sb.AppendLine($"    public static global::Elarion.Abstractions.Dispatch.HandlerDispatcher {HandlersMethodName(moduleName)}(");
        sb.AppendLine("        this global::Elarion.Abstractions.Dispatch.HandlerDispatcher dispatcher)");
        sb.AppendLine("    {");
        foreach (var method in methods)
            RpcMethodEmission.AppendMapHandler(sb, method, "        ", "dispatcher");

        sb.AppendLine("        return dispatcher;");
        sb.AppendLine("    }");
    }

    private static void AppendMcpMetadataMethods(StringBuilder sb, List<ModuleEntry> entries, TransportMaps transport)
    {
        foreach (var entry in entries)
        {
            if (ModuleHasMcp(transport, entry.ModuleName))
                AppendMcpMetadataMethod(sb, entry.ModuleName, transport.RpcByModule[entry.ModuleName]);
        }

        if (McpMetadataEmission.AnyMcp(transport.UnmatchedRpc))
            AppendMcpMetadataMethod(sb, UnmatchedModuleName, transport.UnmatchedRpc);
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

    private static bool ModuleHasHandlers(TransportMaps transport, string moduleName) =>
        transport.RpcByModule.TryGetValue(moduleName, out var methods) && methods.Count > 0;

    private static bool ModuleHasMcp(TransportMaps transport, string moduleName) =>
        transport.RpcByModule.TryGetValue(moduleName, out var methods) && McpMetadataEmission.AnyMcp(methods);

    private static string HttpMethodName(string moduleName) => $"Map{ModuleMethodNamePart(moduleName)}Http";

    private static string HandlersMethodName(string moduleName) => $"Add{ModuleMethodNamePart(moduleName)}Handlers";

    private static string McpMetadataMethodName(string moduleName) => $"Get{ModuleMethodNamePart(moduleName)}McpMetadata";

    private static string ResourceFiltersMethodName(string moduleName) => $"Add{ModuleMethodNamePart(moduleName)}ResourceFilters";

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
