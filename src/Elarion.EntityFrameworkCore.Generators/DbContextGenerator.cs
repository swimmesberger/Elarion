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
    private const string GenerateDbSetsAttributeName = ElarionGeneratorConventions.GenerateDbSetsAttribute;
    private const string DbContextName = "Microsoft.EntityFrameworkCore.DbContext";

    // Two [EntityConfiguration] entities with the same short type name (different namespaces/modules) would
    // pluralize to the same DbSet property name, producing CS0102 in the generated context. Detect the clash,
    // report it, and skip the colliding entities deterministically so the context still compiles.
    private static readonly DiagnosticDescriptor DuplicateDbSetName = new(
        id: "ELEFC002",
        title: "Duplicate DbSet property name",
        messageFormat: "Entities '{0}' and '{1}' both map to DbSet property '{2}' on context '{3}'; rename one entity "
            + "or place it in a differently-scoped context — the colliding DbSet is not generated",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

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
            EntityConfigurationDiscovery.GetScopes(ctx.Attributes),
            DiscoverModelHooks(classSymbol));
    }

    // Each optional Elarion feature that ships its own table (Identity, resource grants, …) opts in with a
    // [GenerateElarion{Feature}] attribute on the context and implements a per-feature model-configuration seam
    // named "OnEntitiesConfigured_{Feature}". Discovering them by convention (not by name) lets any number of
    // features compose onto one context without this generator knowing any of them, and without colliding on the
    // single legacy OnEntitiesConfigured seam.
    private static ImmutableArray<string> DiscoverModelHooks(INamedTypeSymbol classSymbol)
    {
        return classSymbol.GetAttributes()
            .Select(attribute => attribute.AttributeClass?.Name)
            .Where(ElarionGeneratorConventions.IsModelConfigurationFeatureAttribute)
            .Select(name => ElarionGeneratorConventions.ModelConfigurationSeamName(name!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToImmutableArray();
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
        // Every [GenerateDbSets] context gets a ConfigureEntities method (the host calls it from
        // OnModelCreating) and the neutral OnEntitiesConfigured seam — even with no [EntityConfiguration]
        // entities, so an Identity-only context still composes (the Identity generator implements the seam).
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

        // Deterministically resolve DbSet property-name collisions: entities are already sorted by short name,
        // so the first entity to claim a property name keeps it and any later entity mapping to the same name is
        // skipped with a diagnostic. This prevents CS0102 (duplicate member) in the generated context when two
        // [EntityConfiguration] entities share a short type name across namespaces/modules.
        var claimedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var pluralName = Pluralize(entity.Name);
            if (claimedNames.TryGetValue(pluralName, out var owner))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateDbSetName,
                    Location.None,
                    owner,
                    entity.FullName,
                    pluralName,
                    target.FullName));
                continue;
            }

            claimedNames.Add(pluralName, entity.FullName);
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

        sb.AppendLine();
        sb.AppendLine("        // Extensibility seam: hand-written model configuration can implement this partial method.");
        sb.AppendLine("        // Unimplemented, the call is a no-op.");
        sb.AppendLine("        OnEntitiesConfigured(modelBuilder);");

        // One seam per discovered [GenerateElarion{Feature}] attribute, so multiple optional features (Identity,
        // resource grants, …) compose onto one context without colliding on the single legacy seam above.
        foreach (var hook in target.ModelHooks.AsImmutableArray)
        {
            sb.AppendLine(string.Format("        {0}(modelBuilder);", hook));
        }

        if (!entities.IsEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("        // Last, so it also covers entities the seams above added (and their navigation-discovered children).");
            sb.AppendLine("        ApplyElarionClientAssignedGuidKeys(modelBuilder);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Extensibility seam invoked at the end of <see cref=\"ConfigureEntities\"/> so other source");
        sb.AppendLine("    /// generators can append model configuration. Elides to a no-op when unimplemented.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    partial void OnEntitiesConfigured(ModelBuilder modelBuilder);");

        foreach (var hook in target.ModelHooks.AsImmutableArray)
        {
            sb.AppendLine(string.Format("    partial void {0}(ModelBuilder modelBuilder);", hook));
        }

        if (!entities.IsEmpty)
        {
            AppendClientAssignedGuidKeys(sb, entities);
        }

        sb.AppendLine("}");

        context.AddSource(
            string.Format("{0}.DbSets.g.cs", target.Name),
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // The application idiom assigns entity identity in code (Id = Guid.CreateVersion7()), but EF Core's
    // convention declares a Guid PK ValueGeneratedOnAdd — and the insert-vs-update heuristic is defined on
    // that claim, so a new child with a set id added to a *tracked* parent is misread as Modified and
    // SaveChanges issues an UPDATE that hits zero rows (DbUpdateConcurrencyException on a real database;
    // the InMemory provider skips the affected-rows check, so tests stay green). The generated pass declares
    // reality for the discovered domain entities. Scoping is by the *assemblies* of the discovered
    // [EntityConfiguration] entity types — not the type list itself — so navigation-discovered children
    // (exactly where the heuristic detonates) are covered, while Identity/framework entities in other
    // assemblies are never touched. All configuration-source guards run at model-build time, so explicit or
    // data-annotation configuration, custom value generators, and store defaults always win.
    private static void AppendClientAssignedGuidKeys(StringBuilder sb, ImmutableArray<EntityInfo> entities)
    {
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Declares each domain entity's single-property <c>Guid</c> primary key as client-assigned");
        sb.AppendLine("    /// (<c>ValueGenerated.Never</c>): Elarion entities own their ids and mint them in code (for example");
        sb.AppendLine("    /// <c>Guid.CreateVersion7()</c>), so the model must not claim generation — a set value on a \"generated\"");
        sb.AppendLine("    /// key makes EF Core's insert-vs-update heuristic treat a new child added to a tracked parent as an");
        sb.AppendLine("    /// update (UPDATE … 0 rows → DbUpdateConcurrencyException). Scoped to the assemblies of the discovered");
        sb.AppendLine("    /// [EntityConfiguration] entities, so navigation-discovered children are covered while Identity and");
        sb.AppendLine("    /// framework entities keep their packaged generation. Explicit or data-annotation configuration, a");
        sb.AppendLine("    /// custom value generator, and store defaults all win over this pass.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    private static void ApplyElarionClientAssignedGuidKeys(ModelBuilder modelBuilder) {");
        sb.AppendLine("        var domainAssemblies = new global::System.Collections.Generic.HashSet<global::System.Reflection.Assembly> {");
        foreach (var entity in entities)
        {
            sb.AppendLine(string.Format("            typeof(global::{0}).Assembly,", entity.FullName));
        }

        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        foreach (var entityType in global::System.Linq.Enumerable.ToList(modelBuilder.Model.GetEntityTypes())) {");
        sb.AppendLine("            // Derived types share the root's key; owned/keyless and composite keys are out of scope.");
        sb.AppendLine("            if (entityType.BaseType is not null || !domainAssemblies.Contains(entityType.ClrType.Assembly)) {");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            var primaryKey = entityType.FindPrimaryKey();");
        sb.AppendLine("            if (primaryKey is null || primaryKey.Properties.Count != 1) {");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            var property = primaryKey.Properties[0];");
        sb.AppendLine("            if (property.ClrType != typeof(global::System.Guid)) {");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            // Only override EF's convention claim: explicit or data-annotation configuration wins.");
        sb.AppendLine("            var valueGeneratedSource =");
        sb.AppendLine("                ((global::Microsoft.EntityFrameworkCore.Metadata.IConventionProperty)property).GetValueGeneratedConfigurationSource();");
        sb.AppendLine("            if (valueGeneratedSource is not null &&");
        sb.AppendLine("                valueGeneratedSource != global::Microsoft.EntityFrameworkCore.Metadata.ConfigurationSource.Convention) {");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            // A configured value generator or store default is a deliberate generation choice — leave it alone.");
        sb.AppendLine("            if (property.FindAnnotation(\"ValueGeneratorFactory\") is not null ||");
        sb.AppendLine("                property.FindAnnotation(\"ValueGeneratorFactoryType\") is not null ||");
        sb.AppendLine("                property.FindAnnotation(\"Relational:DefaultValueSql\") is not null ||");
        sb.AppendLine("                property.FindAnnotation(\"Relational:DefaultValue\") is not null ||");
        sb.AppendLine("                property.FindAnnotation(\"Relational:ComputedColumnSql\") is not null) {");
        sb.AppendLine("                continue;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            property.ValueGenerated = global::Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
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
        EquatableArray<string> Scopes,
        EquatableArray<string> ModelHooks);

    private readonly record struct EntityInfo(
        string Name,
        string FullName,
        string Namespace);

    private readonly record struct ReferencedData(EquatableArray<ConfigInfo> Configs);
}
