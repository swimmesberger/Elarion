using System.Collections.Immutable;
using System.Text;
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
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetInferredClassTarget(ctx))
            .Where(static target => target is not null);

        var entitiesAndConfigs = context.CompilationProvider
            .Select(static (compilation, _) => CollectEntitiesAndConfigs(compilation));

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

    private static CollectedData CollectEntitiesAndConfigs(Compilation compilation)
    {
        var dbEntityAttribute = compilation.GetTypeByMetadataName(DbEntityAttributeName);
        var configInterface = compilation.GetTypeByMetadataName(EntityTypeConfigurationName);

        var entities = ImmutableArray.CreateBuilder<EntityInfo>();
        var configs = ImmutableArray.CreateBuilder<ConfigInfo>();
        var seenEntities = new HashSet<string>(StringComparer.Ordinal);
        var seenConfigs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();

            foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = semanticModel.GetDeclaredSymbol(classDeclaration);
                if (symbol is not INamedTypeSymbol classSymbol || classSymbol.IsAbstract)
                {
                    continue;
                }

                CollectEntity(classSymbol, dbEntityAttribute, entities, seenEntities);
                CollectConfiguration(classSymbol, configInterface, configs, seenConfigs);
            }
        }

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assemblySymbol)
            {
                continue;
            }

            CollectFromNamespace(
                assemblySymbol.GlobalNamespace,
                dbEntityAttribute,
                configInterface,
                entities,
                configs,
                seenEntities,
                seenConfigs);
        }

        return new CollectedData(
            entities.ToImmutable().Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal)),
            configs.ToImmutable().Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal)));
    }

    private static void CollectFromNamespace(
        INamespaceSymbol ns,
        INamedTypeSymbol? dbEntityAttribute,
        INamedTypeSymbol? configInterface,
        ImmutableArray<EntityInfo>.Builder entities,
        ImmutableArray<ConfigInfo>.Builder configs,
        HashSet<string> seenEntities,
        HashSet<string> seenConfigs)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            {
                continue;
            }

            CollectEntity(type, dbEntityAttribute, entities, seenEntities);
            CollectConfiguration(type, configInterface, configs, seenConfigs);
        }

        foreach (var nestedNs in ns.GetNamespaceMembers())
        {
            CollectFromNamespace(nestedNs, dbEntityAttribute, configInterface, entities, configs, seenEntities, seenConfigs);
        }
    }

    private static void CollectEntity(
        INamedTypeSymbol type,
        INamedTypeSymbol? dbEntityAttribute,
        ImmutableArray<EntityInfo>.Builder entities,
        HashSet<string> seenEntities)
    {
        if (dbEntityAttribute is null)
        {
            return;
        }

        var attribute = TryGetAttribute(type, dbEntityAttribute);
        if (attribute is null || !seenEntities.Add(type.ToDisplayString()))
        {
            return;
        }

        entities.Add(new EntityInfo(
            type.Name,
            type.ToDisplayString(),
            type.ContainingNamespace.ToDisplayString(),
            GetScopes(attribute)));
    }

    private static void CollectConfiguration(
        INamedTypeSymbol type,
        INamedTypeSymbol? configInterface,
        ImmutableArray<ConfigInfo>.Builder configs,
        HashSet<string> seenConfigs)
    {
        if (configInterface is null)
        {
            return;
        }

        var configuredEntity = GetConfiguredEntityType(type, configInterface);
        if (configuredEntity is null || !seenConfigs.Add(type.ToDisplayString()))
        {
            return;
        }

        configs.Add(new ConfigInfo(
            type.ToDisplayString(),
            type.ContainingNamespace.ToDisplayString(),
            configuredEntity));
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
        ImmutableArray<string> Scopes);

    private readonly record struct EntityInfo(
        string Name,
        string FullName,
        string Namespace,
        ImmutableArray<string> Scopes);

    private readonly record struct ConfigInfo(string FullName, string Namespace, string EntityTypeFqn);

    private readonly record struct CollectedData(
        ImmutableArray<EntityInfo> Entities,
        ImmutableArray<ConfigInfo> Configurations);
}
