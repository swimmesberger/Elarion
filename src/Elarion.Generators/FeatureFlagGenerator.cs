using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Turns feature-flag declarations into compile-time guarantees and wiring (ADR-0079). Every flag is declared once —
/// <c>[FeatureFlag]</c> on its resolver class (code-defined) or <c>[BackendFeatureFlag]</c> (backend-evaluated) —
/// and this generator:
/// <list type="bullet">
/// <item>rejects a flag used by <c>[FeatureGate]</c>/<c>[FeatureVariant]</c> but declared nowhere (ELFLAG001), a
/// flag declared twice, including across referenced assemblies (ELFLAG002), a code-defined declaration whose class
/// cannot own the flag (ELFLAG003), a declaration outside every local module (ELFLAG004), and a blank name
/// (ELFLAG005);</item>
/// <item>emits, per module, the registration of its flags into the module's default services — so a flag exists
/// exactly when its module is enabled, with no runtime scanning;</item>
/// <item>with <c>[assembly: GenerateFeatureFlags]</c> (or <c>[UseElarion]</c>), emits the <c>ElarionFeatureFlags</c>
/// registry: typed keys, name constants, and descriptors aggregated across referenced assemblies.</item>
/// </list>
/// Declarations of referenced assemblies are read from the Elarion manifest, never by scanning their symbols.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class FeatureFlagGenerator : IIncrementalGenerator {
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateFeatureFlagsAttribute";
    private const string FlagKeyFqn = "global::Elarion.Abstractions.Features.FeatureFlagKey";
    private const string DescriptorFqn = "global::Elarion.Abstractions.Features.FeatureFlagDescriptor";
    private const string OwnerFqn = "global::Elarion.Abstractions.Features.FeatureFlagOwner";

    private static readonly DiagnosticDescriptor UndeclaredFlag = new(
        "ELFLAG001", "Feature flag is used but not declared",
        "Feature flag '{0}' used by [{1}] is not declared; declare it once with [FeatureFlag] on its resolver class "
        + "or [BackendFeatureFlag] in a module of this assembly or a referenced one",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor DuplicateFlag = new(
        "ELFLAG002", "Feature flag is declared more than once",
        "Feature flag '{0}' is declared by {1} and by {2}; a flag has exactly one declaration and one owner",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor FlagWithoutOwner = new(
        "ELFLAG003", "Feature flag has no owner",
        "[FeatureFlag(\"{0}\")] on '{1}' declares no owner because {2}; a code-defined flag's class must be its "
        + "IFeatureFlagResolver — use [BackendFeatureFlag] to leave evaluation to the host's backend",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor FlagNotInModule = new(
        "ELFLAG004", "Feature flag is not declared under a module",
        "Feature flag '{0}' is declared in '{1}', which is not under any [AppModule] namespace of this assembly, so "
        + "no module registers it; move the declaration under its module",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BlankFlagName = new(
        "ELFLAG005", "Feature flag declares a blank name",
        "A feature flag declaration on '{0}' has a blank name",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor RegistryAccessorCollision = new(
        "ELFLAG006", "Feature flag registry accessor collision",
        "Feature flags '{0}' and '{1}' map to the same ElarionFeatureFlags accessor '{2}'; the second is omitted "
        + "from the typed accessors (every flag remains in ElarionFeatureFlags.All)",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, true);

    private static readonly string[] ReservedRootNames = ["All", "Names", "ElarionFeatureFlags"];

    private sealed record ModuleScope(string Name, string Namespace);

    private sealed record ReferenceData(
        EquatableArray<ElarionManifest.FeatureFlag> Flags,
        EquatableArray<ModuleScope> Modules);

    private static class TrackingNames {
        public const string Code = "FeatureFlagCodeDeclarations";
        public const string Backend = "FeatureFlagBackendDeclarations";
        public const string Gates = "FeatureFlagGateUsages";
        public const string Variants = "FeatureFlagVariantUsages";
        public const string References = "FeatureFlagReferences";
        public const string Combined = "FeatureFlagsCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var codeDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FeatureFlagDiscovery.FeatureFlagAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => FeatureFlagDiscovery.CreateDeclarations(ctx, true))
            .Collect()
            .Select(static (groups, _) => Flatten(groups))
            .WithTrackingName(TrackingNames.Code);

        var backendDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FeatureFlagDiscovery.BackendFeatureFlagAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => FeatureFlagDiscovery.CreateDeclarations(ctx, false))
            .Collect()
            .Select(static (groups, _) => Flatten(groups))
            .WithTrackingName(TrackingNames.Backend);

        var gateUsages = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FeatureFlagDiscovery.FeatureGateAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => FeatureFlagDiscovery.CreateGateUsages(ctx))
            .Collect()
            .Select(static (groups, _) => Flatten(groups))
            .WithTrackingName(TrackingNames.Gates);

        var variantUsages = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => FeatureFlagDiscovery.CreateVariantUsages(ctx))
            .Collect()
            .Select(static (groups, _) => Flatten(groups))
            .WithTrackingName(TrackingNames.Variants);

        var references = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ElarionManifestReader.Read(reference, ct))
            .Collect()
            .Select(static (results, _) => {
                var manifest = ElarionManifest.Data.Combine(results.Select(static r => r.Data));
                return new ReferenceData(
                    manifest.FeatureFlags.ToEquatableArray(),
                    manifest.Modules.Select(static m => new ModuleScope(m.ModuleName, m.Namespace))
                        .ToEquatableArray());
            })
            .WithTrackingName(TrackingNames.References);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);
        var rootNamespace = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? string.Empty)
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) ? ns : null))
            .Select(static (pair, _) => string.IsNullOrEmpty(pair.Right) ? pair.Left : pair.Right!);

        var combined = codeDeclarations.Combine(backendDeclarations).Combine(gateUsages).Combine(variantUsages)
            .Combine(references).Combine(modules).Combine(trigger).Combine(rootNamespace)
            .WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var (((((((code, backend), gates), variants), references), modules), hasTrigger), rootNamespace) = source;
            Emit(spc, code, backend, gates, variants, references, modules, hasTrigger, rootNamespace);
        });
    }

    private static EquatableArray<T> Flatten<T>(System.Collections.Immutable.ImmutableArray<EquatableArray<T>> groups) {
        return groups.SelectMany(static group => group).ToEquatableArray();
    }

    private static void Emit(
        SourceProductionContext spc,
        EquatableArray<FeatureFlagDiscovery.Declaration> code,
        EquatableArray<FeatureFlagDiscovery.Declaration> backend,
        EquatableArray<FeatureFlagDiscovery.Usage> gates,
        EquatableArray<FeatureFlagDiscovery.Usage> variants,
        ReferenceData references,
        EquatableArray<ModuleScanner.Module> localModules,
        bool hasTrigger,
        string rootNamespace) {
        var local = code.Concat(backend)
            .OrderBy(static d => d.Name, StringComparer.Ordinal)
            .ThenBy(static d => d.TypeFqn, StringComparer.Ordinal)
            .ToList();

        foreach (var declaration in local.Where(static d => !d.HasValidName))
            spc.ReportDiagnostic(DiagnosticInfo.Create(BlankFlagName, declaration.Location, Display(declaration.TypeFqn))
                .ToDiagnostic());

        foreach (var declaration in local.Where(static d => d.HasValidName && d.IsCode && d.ResolverProblem is not null))
            spc.ReportDiagnostic(DiagnosticInfo.Create(
                FlagWithoutOwner, declaration.Location, declaration.Name, Display(declaration.TypeFqn),
                declaration.ResolverProblem!).ToDiagnostic());

        // One entry per (source, name) across this assembly and its references; a name with more than one
        // distinct source is a duplicate.
        var candidates = new List<FlagEntry>();
        foreach (var declaration in local.Where(static d => d.HasValidName))
            candidates.Add(new FlagEntry(
                declaration.Name, declaration.Namespace, declaration.Description, declaration.ExposeToClient,
                declaration.IsCode, Display(declaration.TypeFqn), declaration));

        foreach (var flag in references.Flags)
            candidates.Add(new FlagEntry(
                flag.Name, flag.Namespace, flag.Description, flag.ExposeToClient, flag.IsCode,
                $"namespace '{flag.Namespace}' of a referenced assembly", null));

        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in candidates.GroupBy(static c => c.Name, StringComparer.Ordinal)) {
            var entries = group.ToList();
            if (entries.Count < 2) continue;

            duplicateNames.Add(group.Key);
            var ordered = entries.OrderBy(static e => e.Source, StringComparer.Ordinal).ToList();
            var locals = ordered.Where(static e => e.Local is not null).ToList();
            if (locals.Count == 0) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    DuplicateFlag, (Location?)null, group.Key, ordered[0].Source, ordered[1].Source).ToDiagnostic());
                continue;
            }

            foreach (var entry in locals) {
                var other = ordered.First(e => !ReferenceEquals(e, entry));
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    DuplicateFlag, entry.Local!.Location, group.Key, entry.Source, other.Source).ToDiagnostic());
            }
        }

        var moduleScopes = new List<ModuleScope>();
        foreach (var module in localModules) moduleScopes.Add(new ModuleScope(module.Name, module.Namespace));
        var localModuleCount = moduleScopes.Count;
        foreach (var module in references.Modules) moduleScopes.Add(module);

        // ELFLAG004: only a module of this assembly can register the flag (the registration is a hook on that
        // module's generated services class), so the flag's namespace must fall under one of them.
        var registrable = new List<(FlagEntry Entry, ModuleScanner.Module Module)>();
        foreach (var entry in candidates.Where(static c => c.Local is not null)) {
            var module = ModuleScanner.FindBest(entry.Namespace, localModules);
            if (module is null) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    FlagNotInModule, entry.Local!.Location, entry.Name, entry.Source).ToDiagnostic());
                continue;
            }

            if (duplicateNames.Contains(entry.Name) || (entry.IsCode && entry.Local!.ResolverProblem is not null))
                continue;

            registrable.Add((entry, module));
        }

        var declared = new HashSet<string>(candidates.Select(static c => c.Name), StringComparer.Ordinal);
        foreach (var usage in gates.Concat(variants)
                     .OrderBy(static u => u.Name, StringComparer.Ordinal)
                     .ThenBy(static u => u.Attribute, StringComparer.Ordinal))
            if (!declared.Contains(usage.Name))
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    UndeclaredFlag, usage.Location, usage.Name, usage.Attribute).ToDiagnostic());

        foreach (var group in registrable
                     .GroupBy(static r => r.Module)
                     .OrderBy(static g => g.Key.Name, StringComparer.Ordinal))
            EmitModuleRegistration(spc, group.Key, group.Select(static r => r.Entry).ToList());

        if (!hasTrigger) return;

        var unique = candidates
            .Where(c => !duplicateNames.Contains(c.Name))
            .OrderBy(static c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (unique.Count == 0) return;

        EmitRegistry(spc, unique, moduleScopes, rootNamespace);
    }

    private sealed record FlagEntry(
        string Name,
        string Namespace,
        string? Description,
        bool ExposeToClient,
        bool IsCode,
        string Source,
        FeatureFlagDiscovery.Declaration? Local);

    private static void EmitModuleRegistration(
        SourceProductionContext spc, ModuleScanner.Module module, List<FlagEntry> entries) {
        var lines = new List<string>();
        foreach (var entry in entries.OrderBy(static e => e.Name, StringComparer.Ordinal)) {
            var name = Literal(entry.Name);
            var moduleName = Literal(module.Name);
            var description = entry.Description is null ? "null" : Literal(entry.Description);
            var expose = entry.ExposeToClient ? "true" : "false";
            lines.Add(entry.IsCode
                ? $"global::Elarion.Features.FeatureFlagServiceCollectionExtensions.AddElarionFeatureFlag<{entry.Local!.TypeFqn}>(services, {name}, {moduleName}, {description}, {expose});"
                : $"global::Elarion.Features.FeatureFlagServiceCollectionExtensions.AddElarionBackendFeatureFlag(services, {name}, {moduleName}, {description}, {expose});");
        }

        ModuleDefaultsEmitter.EmitFiller(
            spc, module.Namespace, module.TypeName, ModuleDefaultsEmitter.AddFeatureFlagsMethod, "FeatureFlags",
            string.Join("\n        ", lines));
    }

    private static void EmitRegistry(
        SourceProductionContext spc, List<FlagEntry> flags, List<ModuleScope> moduleScopes, string rootNamespace) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.FeatureFlagGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591 // Generated code is not documented; a consuming project that enables GenerateDocumentationFile must not be warned about it.");
        sb.AppendLine();
        if (rootNamespace.Length > 0) {
            sb.AppendLine($"namespace {rootNamespace};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The feature-flag registry: every declared flag this assembly declares or references, as typed keys,");
        sb.AppendLine("/// name constants, and descriptor data. Prefer a key over a string at call sites so an undeclared or");
        sb.AppendLine("/// removed flag fails the build.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static partial class ElarionFeatureFlags");
        sb.AppendLine("{");

        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reserved in ReservedRootNames) claimed[reserved] = "(reserved)";

        var accessors = new List<(string Member, FlagEntry Flag)>();
        foreach (var flag in flags) {
            var member = Pascal(flag.Name);
            if (member.Length == 0) continue;

            if (claimed.TryGetValue(member, out var existing)) {
                spc.ReportDiagnostic(Diagnostic.Create(
                    RegistryAccessorCollision, Location.None, existing, flag.Name, member));
                continue;
            }

            claimed[member] = flag.Name;
            accessors.Add((member, flag));
        }

        sb.AppendLine("    /// <summary>The declared flag names as constants, usable in attribute arguments such as <c>[FeatureGate]</c>.</summary>");
        sb.AppendLine("    public static class Names");
        sb.AppendLine("    {");
        foreach (var (member, flag) in accessors)
            sb.AppendLine($"        public const string {member} = {Literal(flag.Name)};");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var (member, flag) in accessors) {
            sb.AppendLine($"    /// <summary>The '{Escape(flag.Name)}' flag.</summary>");
            sb.AppendLine($"    public static readonly {FlagKeyFqn} {member} = new({Literal(flag.Name)});");
        }

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Every declared flag, ordinally sorted by name.</summary>");
        sb.AppendLine($"    public static global::System.Collections.Generic.IReadOnlyList<{DescriptorFqn}> All {{ get; }} = new {DescriptorFqn}[]");
        sb.AppendLine("    {");
        foreach (var flag in flags) {
            var module = ModuleScanner.FindBest(flag.Namespace,
                moduleScopes.Select(static m => new ModuleScanner.Module(m.Name, m.Namespace, string.Empty, string.Empty))
                    .ToList());
            sb.AppendLine($"        new {DescriptorFqn}");
            sb.AppendLine("        {");
            sb.AppendLine($"            Name = {Literal(flag.Name)},");
            sb.AppendLine($"            Module = {Literal(module?.Name ?? string.Empty)},");
            sb.AppendLine($"            Description = {(flag.Description is null ? "null" : Literal(flag.Description))},");
            sb.AppendLine($"            ExposeToClient = {(flag.ExposeToClient ? "true" : "false")},");
            sb.AppendLine($"            Owner = {OwnerFqn}.{(flag.IsCode ? "Code" : "Backend")},");
            sb.AppendLine("        },");
        }

        sb.AppendLine("    };");
        sb.AppendLine("}");
        spc.AddSource("ElarionFeatureFlags.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Display(string typeFqn) {
        return typeFqn.StartsWith("global::", StringComparison.Ordinal) ? typeFqn.Substring("global::".Length) : typeFqn;
    }

    private static string Pascal(string segment) {
        var sb = new StringBuilder(segment.Length);
        var upperNext = true;
        foreach (var ch in segment)
            if (char.IsLetterOrDigit(ch)) {
                sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
                upperNext = false;
            }
            else {
                upperNext = true;
            }

        if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');

        return sb.ToString();
    }

    private static string Escape(string value) {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Literal(string value) {
        return SymbolDisplay.FormatLiteral(value, true);
    }
}
