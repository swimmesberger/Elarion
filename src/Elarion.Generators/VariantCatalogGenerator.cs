using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the assembly's <c>ElarionVariants</c> registry (triggered by <c>[UseElarion]</c> /
/// <c>[GenerateVariantCatalog]</c>): the compile-time catalog of every
/// <c>[FeatureVariant]</c>/<c>[ConfigurationVariant]</c> switch, aggregated across referenced assemblies from
/// the Elarion manifest — the variant analog of <c>ElarionPermissions</c>. Per switch it emits an accessor
/// class (the selector <c>Key</c> plus one <c>const string</c> per value, usable in attributes like
/// <c>[AllowedValues(...)]</c>) and <c>VariantDescriptor</c> data surfaced as <c>All</c>/<c>ByKey</c>/
/// <c>ByModule</c>/<c>Platform</c>; the host seeds runtime consumers explicitly via
/// <c>AddElarionVariantCatalog(ElarionVariants.All)</c>. Descriptors for variants under no module carry
/// <c>Module = null</c> — the documented placement for platform adapters in an infrastructure assembly.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class VariantCatalogGenerator : IIncrementalGenerator {
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateVariantCatalogAttribute";
    private const string DescriptorFqn = "global::Elarion.Abstractions.Features.VariantDescriptor";
    private const string AxisFqn = "global::Elarion.Abstractions.Features.VariantAxis";
    private const string ListFqn = "global::System.Collections.Generic.IReadOnlyList<" + DescriptorFqn + ">";

    private const string DictionaryFqn =
        "global::System.Collections.Generic.IReadOnlyDictionary<string, " + ListFqn + ">";

    private static readonly DiagnosticDescriptor AccessorCollision = new(
        "ELVAR010", "Variant registry accessor collision",
        "Variant selectors or values '{0}' and '{1}' map to the same ElarionVariants accessor '{2}'; the second "
        + "is omitted from the typed accessors (every entry remains in ElarionVariants.All)",
        "Elarion.Abstractions.Features", DiagnosticSeverity.Warning, true);

    private static readonly string[] ReservedRootNames = ["All", "ByKey", "ByModule", "Platform", "ElarionVariants"];
    private static readonly string[] ReservedAccessorMemberNames = ["Key", "Descriptors"];

    private static class TrackingNames {
        public const string Feature = "VariantCatalogFeature";
        public const string Configuration = "VariantCatalogConfiguration";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var featureVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, false))
            .Collect()
            .WithTrackingName(TrackingNames.Feature);

        var configurationVariants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                VariantDiscovery.ConfigurationVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => VariantDiscovery.CreateVariants(ctx, true))
            .Collect()
            .WithTrackingName(TrackingNames.Configuration);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var rootNamespace = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? string.Empty)
            .Combine(context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) ? ns : null))
            .Select(static (pair, _) => string.IsNullOrEmpty(pair.Right) ? pair.Left : pair.Right!);

        var manifests = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ElarionManifestReader.Read(reference, ct))
            .Collect();

        var combined = featureVariants.Combine(configurationVariants).Combine(modules).Combine(trigger)
            .Combine(rootNamespace).Combine(manifests);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var (((((featureGroups, configurationGroups), moduleList), hasTrigger), rootNamespace), manifests) = source;
            if (!hasTrigger) return;

            Emit(spc, featureGroups.AddRange(configurationGroups), moduleList, rootNamespace, manifests);
        });
    }

    private sealed record DescriptorModel(
        bool IsConfiguration,
        string Key,
        string ContractFqn,
        bool CanReferenceContract,
        IReadOnlyList<string> Values,
        string? DefaultValue,
        bool HasDefault,
        string? Module);

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<EquatableArray<ElarionManifest.Variant>> currentGroups,
        IReadOnlyList<ModuleScanner.Module> modules,
        string rootNamespace,
        ImmutableArray<ManifestReadResult> manifests) {
        var manifest = ElarionManifest.Data.Combine(manifests.Select(static r => r.Data));

        // Modules to resolve against: this assembly's plus every referenced assembly's (from the manifest).
        var moduleScopes = new List<(string Name, string Namespace)>();
        foreach (var module in modules) moduleScopes.Add((module.Name, module.Namespace));

        foreach (var module in manifest.Modules) moduleScopes.Add((module.ModuleName, module.Namespace));

        // Current-compilation entries may reference even an internal contract with typeof (same assembly);
        // manifest entries only when the contract is public where it was declared.
        var entries = new List<(ElarionManifest.Variant Variant, bool CanReferenceContract)>();
        var seenEntries = new HashSet<ElarionManifest.Variant>();
        foreach (var group in currentGroups)
        foreach (var variant in group)
            if (seenEntries.Add(variant))
                entries.Add((variant, true));

        foreach (var variant in manifest.Variants)
            if (seenEntries.Add(variant))
                entries.Add((variant, variant.ContractIsPublic));

        if (entries.Count == 0) return;

        var descriptors = entries
            .GroupBy(static e => (e.Variant.IsConfiguration, e.Variant.SelectorKey, e.Variant.ContractFqn))
            .Select(group => {
                var values = group
                    .Where(static e => e.Variant.Value is not null)
                    .Select(static e => e.Variant.Value!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static v => v, StringComparer.Ordinal)
                    .ToList();
                var defaultValue = group
                    .Where(static e => e.Variant is { IsDefault: true, Value: not null })
                    .Select(static e => e.Variant.Value!)
                    .OrderBy(static v => v, StringComparer.Ordinal)
                    .FirstOrDefault();
                var declarationNamespace = group
                    .Select(static e => e.Variant.Namespace)
                    .OrderBy(static n => n, StringComparer.Ordinal)
                    .First();

                return new DescriptorModel(
                    group.Key.IsConfiguration,
                    group.Key.SelectorKey,
                    group.Key.ContractFqn,
                    group.Any(static e => e.CanReferenceContract),
                    values,
                    defaultValue,
                    group.Any(static e => e.Variant.IsDefault),
                    FindBestModule(declarationNamespace, moduleScopes));
            })
            .OrderBy(static d => d.Key, StringComparer.Ordinal)
            .ThenBy(static d => d.ContractFqn, StringComparer.Ordinal)
            .ThenBy(static d => d.IsConfiguration)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.VariantCatalogGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (rootNamespace.Length > 0) {
            sb.AppendLine($"namespace {rootNamespace};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The variant registry: every [FeatureVariant]/[ConfigurationVariant] switch this assembly");
        sb.AppendLine("/// declares or references, with per-switch value constants and descriptor data. Seed runtime");
        sb.AppendLine("/// consumers via services.AddElarionVariantCatalog(ElarionVariants.All).");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static partial class ElarionVariants");
        sb.AppendLine("{");

        for (var i = 0; i < descriptors.Count; i++) {
            var descriptor = descriptors[i];
            var axis = descriptor.IsConfiguration ? "Configuration" : "Feature";
            var contractExpr = descriptor.CanReferenceContract ? $"typeof({descriptor.ContractFqn})" : "null";
            var contractName = descriptor.ContractFqn.Replace("global::", string.Empty);
            sb.AppendLine($"    private static readonly {DescriptorFqn} D{i} = new {DescriptorFqn}");
            sb.AppendLine("    {");
            sb.AppendLine($"        Axis = {AxisFqn}.{axis},");
            sb.AppendLine($"        Key = {Literal(descriptor.Key)},");
            sb.AppendLine($"        ContractName = {Literal(contractName)},");
            sb.AppendLine($"        Contract = {contractExpr},");
            sb.AppendLine($"        Values = {StringArrayLiteral(descriptor.Values)},");
            sb.AppendLine(
                $"        DefaultValue = {(descriptor.DefaultValue is null ? "null" : Literal(descriptor.DefaultValue))},");
            sb.AppendLine($"        HasDefault = {(descriptor.HasDefault ? "true" : "false")},");
            sb.AppendLine($"        Module = {(descriptor.Module is null ? "null" : Literal(descriptor.Module))},");
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        EmitAccessors(spc, sb, descriptors);

        sb.AppendLine("    /// <summary>Every switch descriptor this assembly declares or references.</summary>");
        sb.AppendLine($"    public static {ListFqn} All {{ get; }} = new {DescriptorFqn}[]");
        sb.AppendLine("    {");
        for (var i = 0; i < descriptors.Count; i++) sb.AppendLine($"        D{i},");

        sb.AppendLine("    };");
        sb.AppendLine();

        EmitByKey(sb, descriptors);
        EmitByModule(sb, descriptors);
        EmitPlatform(sb, descriptors);

        sb.AppendLine("}");
        spc.AddSource("ElarionVariants.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitAccessors(
        SourceProductionContext spc, StringBuilder sb, IReadOnlyList<DescriptorModel> descriptors) {
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reserved in ReservedRootNames) claimed[reserved] = "(reserved)";

        var accessorGroups = descriptors
            .Select(static (descriptor, index) => (Descriptor: descriptor, Index: index))
            .GroupBy(static pair => (pair.Descriptor.IsConfiguration, pair.Descriptor.Key))
            .OrderBy(static group => group.Key.Key, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.IsConfiguration);

        foreach (var group in accessorGroups) {
            var accessorName = Pascal(group.Key.Key);
            if (accessorName.Length == 0) continue;

            if (claimed.TryGetValue(accessorName, out var existing)) {
                spc.ReportDiagnostic(Diagnostic.Create(
                    AccessorCollision, Location.None, existing, group.Key.Key, accessorName));
                continue;
            }

            claimed[accessorName] = group.Key.Key;

            var members = group.OrderBy(static pair => pair.Descriptor.ContractFqn, StringComparer.Ordinal).ToList();
            var values = members
                .SelectMany(static pair => pair.Descriptor.Values)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static v => v, StringComparer.Ordinal)
                .ToList();

            sb.AppendLine(
                $"    /// <summary>The '{group.Key.Key}' switch ({(group.Key.IsConfiguration ? "configuration-selected" : "feature-selected")}).</summary>");
            sb.AppendLine($"    public static class {accessorName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>The selector key.</summary>");
            sb.AppendLine($"        public const string Key = {Literal(group.Key.Key)};");

            var memberClaimed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reserved in ReservedAccessorMemberNames) memberClaimed[reserved] = "(reserved)";

            foreach (var value in values) {
                var memberName = Pascal(value);
                if (memberName.Length == 0) continue;

                if (memberClaimed.TryGetValue(memberName, out var existingValue)) {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        AccessorCollision, Location.None, existingValue, value, $"{accessorName}.{memberName}"));
                    continue;
                }

                memberClaimed[memberName] = value;
                sb.AppendLine($"        public const string {memberName} = {Literal(value)};");
            }

            sb.AppendLine();
            sb.AppendLine(
                "        /// <summary>The switch's descriptors (several contracts may share one key).</summary>");
            sb.Append($"        public static {ListFqn} Descriptors {{ get; }} = new {DescriptorFqn}[] {{ ");
            sb.Append(string.Join(", ", members.Select(static pair => $"D{pair.Index}")));
            sb.AppendLine(" };");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }

    private static void EmitByKey(StringBuilder sb, IReadOnlyList<DescriptorModel> descriptors) {
        sb.AppendLine(
            "    /// <summary>Descriptors by selector key (case-insensitive, matching configuration keys).</summary>");
        sb.AppendLine($"    public static {DictionaryFqn} ByKey {{ get; }} =");
        sb.AppendLine(
            $"        new global::System.Collections.Generic.Dictionary<string, {ListFqn}>(global::System.StringComparer.OrdinalIgnoreCase)");
        sb.AppendLine("        {");
        var groups = descriptors
            .Select(static (descriptor, index) => (Descriptor: descriptor, Index: index))
            .GroupBy(static pair => pair.Descriptor.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups) {
            var key = group.Select(static pair => pair.Descriptor.Key).OrderBy(static k => k, StringComparer.Ordinal)
                .First();
            var refs = string.Join(", ",
                group.OrderBy(static pair => pair.Index).Select(static pair => $"D{pair.Index}"));
            sb.AppendLine($"            [{Literal(key)}] = new {DescriptorFqn}[] {{ {refs} }},");
        }

        sb.AppendLine("        };");
        sb.AppendLine();
    }

    private static void EmitByModule(StringBuilder sb, IReadOnlyList<DescriptorModel> descriptors) {
        sb.AppendLine(
            "    /// <summary>Descriptors grouped by owning module (platform variants are under <see cref=\"Platform\"/>).</summary>");
        sb.AppendLine($"    public static {DictionaryFqn} ByModule {{ get; }} =");
        sb.AppendLine(
            $"        new global::System.Collections.Generic.Dictionary<string, {ListFqn}>(global::System.StringComparer.Ordinal)");
        sb.AppendLine("        {");
        var groups = descriptors
            .Select(static (descriptor, index) => (Descriptor: descriptor, Index: index))
            .Where(static pair => pair.Descriptor.Module is not null)
            .GroupBy(static pair => pair.Descriptor.Module!, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups) {
            var refs = string.Join(", ",
                group.OrderBy(static pair => pair.Index).Select(static pair => $"D{pair.Index}"));
            sb.AppendLine($"            [{Literal(group.Key)}] = new {DescriptorFqn}[] {{ {refs} }},");
        }

        sb.AppendLine("        };");
        sb.AppendLine();
    }

    private static void EmitPlatform(StringBuilder sb, IReadOnlyList<DescriptorModel> descriptors) {
        sb.AppendLine(
            "    /// <summary>Descriptors of platform variants — implementations living outside every module.</summary>");
        sb.Append($"    public static {ListFqn} Platform {{ get; }} = new {DescriptorFqn}[] {{ ");
        var refs = descriptors
            .Select(static (descriptor, index) => (Descriptor: descriptor, Index: index))
            .Where(static pair => pair.Descriptor.Module is null)
            .OrderBy(static pair => pair.Index)
            .Select(static pair => $"D{pair.Index}");
        sb.Append(string.Join(", ", refs));
        sb.AppendLine(" };");
    }

    private static string? FindBestModule(string ns, IReadOnlyList<(string Name, string Namespace)> moduleScopes) {
        (string Name, string Namespace)? best = null;
        foreach (var module in moduleScopes) {
            if (!ModuleScanner.IsInScope(ns, module.Namespace) ||
                (best is not null && module.Namespace.Length <= best.Value.Namespace.Length))
                continue;

            best = module;
        }

        return best?.Name;
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

    private static string StringArrayLiteral(IReadOnlyList<string> values) {
        if (values.Count == 0) return "global::System.Array.Empty<string>()";

        return "new string[] { " + string.Join(", ", values.Select(Literal)) + " }";
    }

    private static string Literal(string value) {
        return SymbolDisplay.FormatLiteral(value, true);
    }
}
