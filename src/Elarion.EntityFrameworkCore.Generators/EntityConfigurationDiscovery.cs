using System.Collections.Immutable;
using Elarion.Generators;
using Microsoft.CodeAnalysis;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Shared discovery of in-compilation <c>[EntityConfiguration]</c> classes. Both
/// <see cref="DbContextGenerator"/> (which emits the <c>DbSet&lt;T&gt;</c> members) and
/// <see cref="EntityConfigurationManifestGenerator"/> (which advertises the configurations to other
/// assemblies) run this same transform over the same syntax trigger, so the logic lives in one place and the
/// two generators cannot drift. Diagnostics are returned as data on <see cref="ConfigResult"/>; only
/// <see cref="DbContextGenerator"/> reports them, so a misuse warning is never duplicated.
/// </summary>
internal static class EntityConfigurationDiscovery
{
    public const string AttributeName = "Elarion.EntityFrameworkCore.EntityConfigurationAttribute";
    public const string EntityTypeConfigurationName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";

    public static readonly DiagnosticDescriptor NoConfiguration = new(
        id: "ELEFC001",
        title: "EntityConfiguration must implement IEntityTypeConfiguration<T>",
        messageFormat: "Type '{0}' is annotated with [EntityConfiguration] but implements no IEntityTypeConfiguration<T>; no DbSet or configuration will be generated",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static ConfigResult CreateConfig(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || type.IsAbstract)
        {
            return default;
        }

        var configInterface = ctx.SemanticModel.Compilation.GetTypeByMetadataName(EntityTypeConfigurationName);
        if (configInterface is null)
        {
            return default;
        }

        var entities = GetConfiguredEntities(type, configInterface);
        if (entities.IsEmpty)
        {
            // [EntityConfiguration] on a class that configures nothing yields no DbSet/configuration.
            return new ConfigResult(
                null,
                DiagnosticInfo.Create(NoConfiguration, LocationInfo.From(type), type.Name));
        }

        return new ConfigResult(
            new ConfigInfo(
                type.ToDisplayString(),
                type.ContainingNamespace.ToDisplayString(),
                entities,
                GetScopes(ctx.Attributes)),
            null);
    }

    private static ImmutableArray<ConfiguredEntityInfo> GetConfiguredEntities(
        INamedTypeSymbol type,
        INamedTypeSymbol configInterface)
    {
        var builder = ImmutableArray.CreateBuilder<ConfiguredEntityInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.OriginalDefinition.Equals(configInterface, SymbolEqualityComparer.Default) ||
                iface.TypeArguments.Length != 1 ||
                iface.TypeArguments[0] is not INamedTypeSymbol entity)
            {
                continue;
            }

            var fullName = entity.ToDisplayString();
            if (seen.Add(fullName))
            {
                builder.Add(new ConfiguredEntityInfo(
                    entity.Name,
                    fullName,
                    entity.ContainingNamespace.ToDisplayString()));
            }
        }

        // Order independently of AllInterfaces iteration so the emitted output is byte-identical.
        builder.Sort(static (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        return builder.ToImmutable();
    }

    public static ImmutableArray<string> GetScopes(ImmutableArray<AttributeData> attributes)
    {
        if (attributes.IsEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        return GetScopes(attributes[0]);
    }

    public static ImmutableArray<string> GetScopes(AttributeData attribute)
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
}

internal readonly record struct ConfiguredEntityInfo(
    string Name,
    string FullName,
    string Namespace);

internal readonly record struct ConfigInfo(
    string FullName,
    string Namespace,
    EquatableArray<ConfiguredEntityInfo> Entities,
    EquatableArray<string> Scopes);

internal readonly record struct ConfigResult(ConfigInfo? Config, DiagnosticInfo? Diagnostic);
