using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Source generator that produces DbSet properties for interfaces marked with
/// [GenerateDbSets] and for DbContext classes implementing those interfaces.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DbContextGenerator : IIncrementalGenerator
{
    private const string GenerateDbSetsAttributeName = "Elarion.EntityFrameworkCore.GenerateDbSetsAttribute";
    private const string DbEntityAttributeName = "Elarion.EntityFrameworkCore.DbEntityAttribute";
    private const string DbContextName = "Microsoft.EntityFrameworkCore.DbContext";
    private const string EntityTypeConfigurationName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaceTargets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                GenerateDbSetsAttributeName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (ctx, _) => GetGeneratedInterfaceTarget(ctx));

        var inferredClassTargets = context.SyntaxProvider
            .CreateSyntaxProvider(
                // Narrow to plausible DbContext-derived partials syntactically before paying for the
                // semantic transform; GetInferredClassTarget still does the authoritative confirmation.
                static (node, _) => node is ClassDeclarationSyntax c
                    && c.Modifiers.Any(SyntaxKind.PartialKeyword)
                    && c.BaseList is not null,
                static (ctx, _) => GetInferredClassTarget(ctx))
            .Where(static target => target is not null);

        // In-compilation entities/configs are discovered through the syntax providers (incremental: only the
        // edited declaration re-runs). Entities/configs in referenced assemblies are not visible to those, so a
        // separate pass reads them; its result is value-equatable so it does not force a re-emit.
        var inCompilationEntities = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                DbEntityAttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateEntity(ctx))
            .Where(static entity => entity is not null)
            .Select(static (entity, _) => entity!.Value)
            .Collect();

        var inCompilationConfigs = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => CreateConfig(ctx, ct))
            .Where(static config => config is not null)
            .Select(static (config, _) => config!.Value)
            .Collect();

        // Referenced entities are read from each reference's emitted manifest (a per-assembly metadata
        // attribute) instead of scanning its symbol tree. MetadataReferencesProvider caches per reference, so
        // a source edit re-reads nothing — only a changed reference is re-read.
        var referencedData = context.MetadataReferencesProvider
            .Select(static (reference, ct) => ReadManifest(reference, ct))
            .Collect()
            .WithTrackingName("ReferencedEntities");

        // Advertise this assembly's entities/configurations so a DbContext in another assembly can read them.
        context.RegisterSourceOutput(
            inCompilationEntities.Combine(inCompilationConfigs),
            static (spc, pair) => EmitManifest(spc, pair.Left, pair.Right));

        var entitiesAndConfigs = inCompilationEntities
            .Combine(inCompilationConfigs)
            .Combine(referencedData)
            .Select(static (source, _) => MergeCollected(source.Left.Left, source.Left.Right, source.Right))
            .WithTrackingName("EntitiesAndConfigs");

        context.RegisterSourceOutput(
            interfaceTargets.Collect().Combine(entitiesAndConfigs),
            static (spc, pair) =>
            {
                var (targets, data) = pair;
                foreach (var target in DistinctTargets(targets))
                {
                    var entities = SelectEntities(data.Entities, target);
                    GenerateDbContextInterface(spc, target, entities);
                }
            });

        context.RegisterSourceOutput(
            inferredClassTargets.Collect().Combine(entitiesAndConfigs),
            static (spc, pair) =>
            {
                var (targets, data) = pair;
                foreach (var target in DistinctNullableTargets(targets))
                {
                    var entities = SelectEntities(data.Entities, target);
                    var configs = SelectConfigurations(data.Configurations, entities, target);
                    GenerateDbContextClass(spc, target, entities, configs);
                }
            });
    }

    private static TargetInfo GetGeneratedInterfaceTarget(GeneratorAttributeSyntaxContext ctx)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var scopes = GetScopes(ctx.Attributes);

        return new TargetInfo(
            symbol.Name,
            symbol.ContainingNamespace.ToDisplayString(),
            symbol.ToDisplayString(),
            scopes);
    }

    private static TargetInfo? GetInferredClassTarget(GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDeclaration ||
            !classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return null;
        }

        if (ctx.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol ||
            classSymbol.IsAbstract)
        {
            return null;
        }

        var compilation = ctx.SemanticModel.Compilation;
        var dbContext = compilation.GetTypeByMetadataName(DbContextName);
        var generateDbSetsAttribute = compilation.GetTypeByMetadataName(GenerateDbSetsAttributeName);
        if (dbContext is null ||
            generateDbSetsAttribute is null ||
            !InheritsFrom(classSymbol, dbContext))
        {
            return null;
        }

        var generatedInterfaces = classSymbol.AllInterfaces
            .Select(interfaceSymbol => TryGetAttribute(interfaceSymbol, generateDbSetsAttribute))
            .Where(attribute => attribute is not null)
            .ToImmutableArray();

        if (generatedInterfaces.IsEmpty)
        {
            return null;
        }

        return new TargetInfo(
            classSymbol.Name,
            classSymbol.ContainingNamespace.ToDisplayString(),
            classSymbol.ToDisplayString(),
            CombineInterfaceScopes(generatedInterfaces));
    }

    private static EntityInfo? CreateEntity(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || type.IsAbstract || ctx.Attributes.Length == 0)
        {
            return null;
        }

        return new EntityInfo(
            type.Name,
            type.ToDisplayString(),
            type.ContainingNamespace.ToDisplayString(),
            GetScopes(ctx.Attributes[0]));
    }

    private static ConfigInfo? CreateConfig(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDeclaration ||
            ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) is not INamedTypeSymbol type ||
            type.IsAbstract)
        {
            return null;
        }

        var configInterface = ctx.SemanticModel.Compilation.GetTypeByMetadataName(EntityTypeConfigurationName);
        if (configInterface is null)
        {
            return null;
        }

        var configuredEntity = GetConfiguredEntityType(type, configInterface);
        if (configuredEntity is null)
        {
            return null;
        }

        return new ConfigInfo(type.ToDisplayString(), type.ContainingNamespace.ToDisplayString(), configuredEntity);
    }

    private static ReferencedData ReadManifest(MetadataReference reference, CancellationToken ct)
    {
        var entities = ImmutableArray.CreateBuilder<EntityInfo>();
        var configs = ImmutableArray.CreateBuilder<ConfigInfo>();

        foreach (var (key, value) in DbEntityManifest.ReadEntries(reference, ct))
        {
            switch (key)
            {
                case DbEntityManifest.EntityKey:
                    if (TryDecodeEntity(value, out var entity))
                    {
                        entities.Add(entity);
                    }

                    break;
                case DbEntityManifest.ConfigKey:
                    if (TryDecodeConfig(value, out var config))
                    {
                        configs.Add(config);
                    }

                    break;
            }
        }

        return new ReferencedData(entities.ToImmutable(), configs.ToImmutable());
    }

    private static bool TryDecodeEntity(string value, out EntityInfo entity)
    {
        entity = default;
        if (!DbEntityManifest.TryDecodeFields(value, out var fields) ||
            fields.Count < 3 ||
            fields[0] is null || fields[1] is null || fields[2] is null)
        {
            return false;
        }

        var scopes = ImmutableArray.CreateBuilder<string>();
        for (var i = 3; i < fields.Count; i++)
        {
            if (fields[i] is { } scope)
            {
                scopes.Add(scope);
            }
        }

        entity = new EntityInfo(fields[0]!, fields[1]!, fields[2]!, scopes.ToImmutable());
        return true;
    }

    private static bool TryDecodeConfig(string value, out ConfigInfo config)
    {
        config = default;
        if (!DbEntityManifest.TryDecodeFields(value, out var fields) ||
            fields.Count != 3 ||
            fields[0] is null || fields[1] is null || fields[2] is null)
        {
            return false;
        }

        config = new ConfigInfo(fields[0]!, fields[1]!, fields[2]!);
        return true;
    }

    private static void EmitManifest(
        SourceProductionContext spc,
        ImmutableArray<EntityInfo> entities,
        ImmutableArray<ConfigInfo> configs)
    {
        if (entities.IsEmpty && configs.IsEmpty)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.EntityFrameworkCore.Generators.DbContextGenerator (entity manifest)");
        sb.AppendLine("// Do not edit this file manually.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        foreach (var entity in entities.OrderBy(static e => e.FullName, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(
                sb,
                DbEntityManifest.EntityKey,
                DbEntityManifest.EncodeEntity(entity.Name, entity.FullName, entity.Namespace, entity.Scopes));
        }

        foreach (var config in configs.OrderBy(static c => c.FullName, StringComparer.Ordinal))
        {
            AppendAssemblyMetadata(
                sb,
                DbEntityManifest.ConfigKey,
                DbEntityManifest.EncodeConfig(config.FullName, config.Namespace, config.EntityTypeFqn));
        }

        spc.AddSource("ElarionDbEntityManifest.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendAssemblyMetadata(StringBuilder sb, string key, string value)
    {
        sb.Append("[assembly: global::System.Reflection.AssemblyMetadataAttribute(");
        sb.Append(SymbolDisplay.FormatLiteral(key, quote: true));
        sb.Append(", ");
        sb.Append(SymbolDisplay.FormatLiteral(value, quote: true));
        sb.AppendLine(")]");
    }

    private static CollectedData MergeCollected(
        ImmutableArray<EntityInfo> inCompilationEntities,
        ImmutableArray<ConfigInfo> inCompilationConfigs,
        ImmutableArray<ReferencedData> referenced)
    {
        var entities = ImmutableArray.CreateBuilder<EntityInfo>();
        var configs = ImmutableArray.CreateBuilder<ConfigInfo>();
        var seenEntities = new HashSet<string>(StringComparer.Ordinal);
        var seenConfigs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in inCompilationEntities)
        {
            if (seenEntities.Add(entity.FullName))
            {
                entities.Add(entity);
            }
        }

        foreach (var config in inCompilationConfigs)
        {
            if (seenConfigs.Add(config.FullName))
            {
                configs.Add(config);
            }
        }

        foreach (var data in referenced)
        {
            foreach (var entity in data.Entities)
            {
                if (seenEntities.Add(entity.FullName))
                {
                    entities.Add(entity);
                }
            }

            foreach (var config in data.Configs)
            {
                if (seenConfigs.Add(config.FullName))
                {
                    configs.Add(config);
                }
            }
        }

        return new CollectedData(
            entities.ToImmutable().Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal)),
            configs.ToImmutable().Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal)));
    }

    private static string? GetConfiguredEntityType(INamedTypeSymbol type, INamedTypeSymbol configInterface)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.Equals(configInterface, SymbolEqualityComparer.Default) &&
                iface.TypeArguments.Length == 1)
            {
                return iface.TypeArguments[0].ToDisplayString();
            }
        }

        return null;
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

    private static AttributeData? TryGetAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        return symbol
            .GetAttributes()
            .FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType));
    }

    private static ImmutableArray<string> GetScopes(ImmutableArray<AttributeData> attributes)
    {
        if (attributes.IsEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        return GetScopes(attributes[0]);
    }

    private static ImmutableArray<string> GetScopes(AttributeData attribute)
    {
        var scopes = ImmutableArray.CreateBuilder<string>();
        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Array)
            {
                foreach (var value in argument.Values)
                {
                    AddScope(value, scopes);
                }
            }
            else
            {
                AddScope(argument, scopes);
            }
        }

        return NormalizeScopes(scopes);
    }

    private static void AddScope(TypedConstant value, ImmutableArray<string>.Builder scopes)
    {
        if (value.Value is string scope && scope.Length > 0)
        {
            scopes.Add(scope);
        }
    }

    private static ImmutableArray<string> CombineInterfaceScopes(ImmutableArray<AttributeData?> attributes)
    {
        var scopes = ImmutableArray.CreateBuilder<string>();
        foreach (var attribute in attributes)
        {
            if (attribute is null)
            {
                continue;
            }

            var interfaceScopes = GetScopes(attribute);
            if (interfaceScopes.IsEmpty)
            {
                return ImmutableArray<string>.Empty;
            }

            scopes.AddRange(interfaceScopes);
        }

        return NormalizeScopes(scopes);
    }

    private static ImmutableArray<string> NormalizeScopes(IEnumerable<string> scopes)
    {
        var normalized = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (seen.Add(scope))
            {
                normalized.Add(scope);
            }
        }

        normalized.Sort(StringComparer.Ordinal);
        return normalized.ToImmutable();
    }

    private static ImmutableArray<EntityInfo> SelectEntities(ImmutableArray<EntityInfo> entities, TargetInfo target)
    {
        if (target.Scopes.IsEmpty)
        {
            return entities;
        }

        var targetScopes = new HashSet<string>(target.Scopes, StringComparer.Ordinal);
        return entities
            .Where(entity => entity.Scopes.Any(targetScopes.Contains))
            .ToImmutableArray();
    }

    private static ImmutableArray<ConfigInfo> SelectConfigurations(
        ImmutableArray<ConfigInfo> configurations,
        ImmutableArray<EntityInfo> entities,
        TargetInfo target)
    {
        if (target.Scopes.IsEmpty)
        {
            return configurations;
        }

        var entityNames = new HashSet<string>(
            entities.Select(entity => entity.FullName),
            StringComparer.Ordinal);

        return configurations
            .Where(config => entityNames.Contains(config.EntityTypeFqn))
            .ToImmutableArray();
    }

    private static IEnumerable<TargetInfo> DistinctTargets(ImmutableArray<TargetInfo> targets)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (seen.Add(target.FullName))
            {
                yield return target;
            }
        }
    }

    private static IEnumerable<TargetInfo> DistinctNullableTargets(ImmutableArray<TargetInfo?> targets)
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
            .Concat(configs.Select(c => c.Namespace))
            .Where(ns => !string.IsNullOrEmpty(ns) && ns != target.Namespace)
            .Distinct()
            .OrderBy(ns => ns);

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
        foreach (var config in configs)
        {
            sb.AppendLine(string.Format(
                "        new global::{0}().Configure(modelBuilder.Entity<global::{1}>());",
                config.FullName,
                config.EntityTypeFqn));
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource(
            string.Format("{0}.DbSets.g.cs", target.Name),
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void GenerateDbContextInterface(
        SourceProductionContext context,
        TargetInfo target,
        ImmutableArray<EntityInfo> entities)
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
            .OrderBy(ns => ns);

        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        foreach (var ns in namespaces)
        {
            sb.AppendLine(string.Format("using {0};", ns));
        }

        sb.AppendLine();
        sb.AppendLine(string.Format("namespace {0};", target.Namespace));
        sb.AppendLine();
        sb.AppendLine(string.Format("partial interface {0} {{", target.Name));

        foreach (var entity in entities)
        {
            var pluralName = Pluralize(entity.Name);
            sb.AppendLine(string.Format("    DbSet<{0}> {1} {{ get; }}", entity.Name, pluralName));
        }

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
        string Namespace,
        EquatableArray<string> Scopes);

    private readonly record struct ConfigInfo(string FullName, string Namespace, string EntityTypeFqn);

    private readonly record struct CollectedData(
        EquatableArray<EntityInfo> Entities,
        EquatableArray<ConfigInfo> Configurations);

    private readonly record struct ReferencedData(
        EquatableArray<EntityInfo> Entities,
        EquatableArray<ConfigInfo> Configs);
}
