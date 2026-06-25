using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Source generator that fills a partial <c>DbContext</c> class marked with <c>[GenerateDbSets]</c> with
/// <c>DbSet&lt;T&gt;</c> properties and a <c>ConfigureEntities(ModelBuilder)</c> method. The DbSets are a
/// concern of the <em>implementation</em> (the concrete context), not of an interface — the database is
/// application logic that handlers use directly, so there is no generated context abstraction. The entity
/// set is driven entirely by <c>[EntityConfiguration]</c> classes — each implemented
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> contributes a <c>DbSet&lt;T&gt;</c> and a direct
/// <c>Configure(...)</c> call. There is no separate entity marker: a configured entity is a discovered
/// entity.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DbContextGenerator : IIncrementalGenerator
{
    private const string GenerateDbSetsAttributeName = "Elarion.EntityFrameworkCore.GenerateDbSetsAttribute";
    private const string DbContextName = "Microsoft.EntityFrameworkCore.DbContext";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The target is the partial DbContext class carrying [GenerateDbSets]; the DbSets are emitted onto
        // the implementation, not an interface.
        var classTargets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateDbSetsAttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetClassTarget(ctx))
            .Where(static target => target is not null);

        // In-compilation configurations are discovered through the syntax provider (incremental: only the
        // edited declaration re-runs). Configurations in referenced assemblies are not visible to it, so a
        // separate pass reads them from each reference's manifest; that result is value-equatable so it does
        // not force a re-emit.
        var configResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EntityConfigurationDiscovery.AttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => EntityConfigurationDiscovery.CreateConfig(ctx, ct));

        // Diagnostics are data: the transform stays pure and the misuse warning is reported here.
        context.RegisterSourceOutput(configResults, static (spc, result) =>
        {
            if (result.Diagnostic is { } diagnostic)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
        });

        var inCompilationConfigs = configResults
            .Where(static result => result.Config is not null)
            .Select(static (result, _) => result.Config!.Value)
            .Collect();

        // Referenced configurations are read from each reference's emitted manifest (a per-assembly metadata
        // attribute) instead of scanning its symbol tree. MetadataReferencesProvider caches per reference, so
        // a source edit re-reads nothing — only a changed reference is re-read.
        var referencedConfigs = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ReadManifest(reference, ct))
            .Collect()
            .WithTrackingName("ReferencedConfigs");

        // This assembly's configurations are advertised to other assemblies by the dedicated
        // EntityConfigurationManifestGenerator. This generator emits only DbSet members onto the partial
        // context — it does not mutate the compilation's assembly attributes — so its output refreshes
        // reliably in IDE source-generator hosts.
        var configurations = inCompilationConfigs
            .Combine(referencedConfigs)
            .Select(static (source, _) => Merge(source.Left, source.Right))
            .WithTrackingName("Configurations");

        context.RegisterSourceOutput(
            classTargets.Collect().Combine(configurations),
            static (spc, pair) =>
            {
                var (targets, configs) = pair;
                foreach (var target in DistinctTargets(targets))
                {
                    var active = SelectActiveConfigs(configs, target);
                    var entities = DeriveEntities(active);
                    GenerateDbContextClass(spc, target, entities, active);
                }
            });
    }

    private static TargetInfo? GetClassTarget(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract)
        {
            return null;
        }

        if (ctx.TargetNode is not ClassDeclarationSyntax classDeclaration ||
            !classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return null;
        }

        var dbContext = ctx.SemanticModel.Compilation.GetTypeByMetadataName(DbContextName);
        if (dbContext is null || !InheritsFrom(classSymbol, dbContext))
        {
            return null;
        }

        return new TargetInfo(
            classSymbol.Name,
            classSymbol.ContainingNamespace.ToDisplayString(),
            classSymbol.ToDisplayString(),
            EntityConfigurationDiscovery.GetScopes(ctx.Attributes));
    }

    private static ReferencedData ReadManifest(MetadataReference reference, CancellationToken ct)
    {
        var configs = ImmutableArray.CreateBuilder<ConfigInfo>();

        foreach (var (key, value) in EntityConfigurationManifest.ReadEntries(reference, ct))
        {
            if (key != EntityConfigurationManifest.ConfigKey)
            {
                continue;
            }

            if (EntityConfigurationManifest.TryDecodeConfig(value, out var fullName, out var ns, out var scopes, out var entities))
            {
                var entityInfos = entities
                    .Select(static e => new ConfiguredEntityInfo(e.Name, e.FullName, e.Namespace))
                    .ToImmutableArray();
                configs.Add(new ConfigInfo(fullName, ns, entityInfos, scopes.ToImmutableArray()));
            }
        }

        return new ReferencedData(configs.ToImmutable());
    }

    private static EquatableArray<ConfigInfo> Merge(
        ImmutableArray<ConfigInfo> inCompilation,
        ImmutableArray<ReferencedData> referenced)
    {
        var configs = ImmutableArray.CreateBuilder<ConfigInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var config in inCompilation)
        {
            if (seen.Add(config.FullName))
            {
                configs.Add(config);
            }
        }

        foreach (var data in referenced)
        {
            foreach (var config in data.Configs)
            {
                if (seen.Add(config.FullName))
                {
                    configs.Add(config);
                }
            }
        }

        return configs.ToImmutable().Sort(static (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
    }

    private static ImmutableArray<ConfigInfo> SelectActiveConfigs(EquatableArray<ConfigInfo> configs, TargetInfo target)
    {
        // An unscoped context applies every configuration; a scoped context applies only configurations
        // whose scopes intersect (a global/unscoped configuration therefore appears only in unscoped
        // contexts, matching the historical entity-scope semantics).
        if (target.Scopes.IsEmpty)
        {
            return configs.AsImmutableArray;
        }

        var targetScopes = new HashSet<string>(target.Scopes.AsImmutableArray, StringComparer.Ordinal);
        return configs.AsImmutableArray
            .Where(config => config.Scopes.AsImmutableArray.Any(targetScopes.Contains))
            .ToImmutableArray();
    }

    private static ImmutableArray<EntityInfo> DeriveEntities(ImmutableArray<ConfigInfo> activeConfigs)
    {
        var entities = ImmutableArray.CreateBuilder<EntityInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var config in activeConfigs)
        {
            foreach (var entity in config.Entities)
            {
                if (seen.Add(entity.FullName))
                {
                    entities.Add(new EntityInfo(entity.Name, entity.FullName, entity.Namespace));
                }
            }
        }

        entities.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return entities.ToImmutable();
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TargetInfo> DistinctTargets(ImmutableArray<TargetInfo?> targets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.HasValue && seen.Add(target.Value.FullName))
            {
                yield return target.Value;
            }
        }
    }

    private static void GenerateDbContextClass(
        SourceProductionContext context,
        TargetInfo target,
        ImmutableArray<EntityInfo> entities,
        ImmutableArray<ConfigInfo> configs)
    {
        if (entities.IsEmpty)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        var namespaces = entities
            .Select(e => e.Namespace)
            .Where(ns => !string.IsNullOrEmpty(ns) && ns != target.Namespace)
            .Distinct()
            .OrderBy(ns => ns, StringComparer.Ordinal);

        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        foreach (var ns in namespaces)
        {
            sb.AppendLine(string.Format("using {0};", ns));
        }

        sb.AppendLine();
        sb.AppendLine(string.Format("namespace {0};", target.Namespace));
        sb.AppendLine();
        sb.AppendLine(string.Format("partial class {0} {{", target.Name));

        foreach (var entity in entities)
        {
            var pluralName = Pluralize(entity.Name);
            sb.AppendLine(string.Format("    public DbSet<{0}> {1} => Set<{0}>();", entity.Name, pluralName));
        }

        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Applies all entity configurations. AOT-compatible (no reflection).");
        sb.AppendLine("    /// Call this from OnModelCreating.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private void ConfigureEntities(ModelBuilder modelBuilder) {");

        var configIndex = 0;
        foreach (var config in configs)
        {
            // One instance per configuration, reused across each entity it configures. The explicit
            // generic argument disambiguates a configuration that implements IEntityTypeConfiguration<T>
            // more than once (and works for explicit interface implementations).
            var local = string.Format("__config{0}", configIndex++);
            sb.AppendLine(string.Format("        var {0} = new global::{1}();", local, config.FullName));
            foreach (var entity in config.Entities)
            {
                sb.AppendLine(string.Format(
                    "        modelBuilder.ApplyConfiguration<global::{0}>({1});",
                    entity.FullName,
                    local));
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource(
            string.Format("{0}.DbSets.g.cs", target.Name),
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (name.EndsWith("ings", StringComparison.Ordinal) ||
            name.EndsWith("ies", StringComparison.Ordinal) ||
            (name.EndsWith("es", StringComparison.Ordinal) && !name.EndsWith("ss", StringComparison.Ordinal)))
        {
            return name;
        }

        if (name.EndsWith("y", StringComparison.Ordinal) && name.Length > 1 &&
            !IsVowel(name[name.Length - 2]))
        {
            return name.Substring(0, name.Length - 1) + "ies";
        }

        if (name.EndsWith("ss", StringComparison.Ordinal) ||
            name.EndsWith("x", StringComparison.Ordinal) ||
            name.EndsWith("ch", StringComparison.Ordinal) ||
            name.EndsWith("sh", StringComparison.Ordinal))
        {
            return name + "es";
        }

        if (name.EndsWith("s", StringComparison.Ordinal))
        {
            return name;
        }

        return name + "s";
    }

    private static bool IsVowel(char c)
    {
        return "aeiouAEIOU".IndexOf(c) >= 0;
    }

    private readonly record struct TargetInfo(
        string Name,
        string Namespace,
        string FullName,
        EquatableArray<string> Scopes);

    private readonly record struct EntityInfo(
        string Name,
        string FullName,
        string Namespace);

    private readonly record struct ReferencedData(EquatableArray<ConfigInfo> Configs);
}
