using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Source generator that fills every partial class annotated with <c>[Keyset&lt;TEntity&gt;]</c> with a
/// strongly-typed keyset (seek) pagination definition. The class is completed as an
/// <c>IKeysetDefinition&lt;TEntity&gt;</c> with a reflection-free ordering, seek predicate, and opaque
/// cursor codec — plain typed expressions the query provider translates to SQL like hand-written code —
/// plus a static <c>Definition</c> singleton. Keyset definitions live off the entity, so one entity can
/// have any number of orderings.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class KeysetGenerator : IIncrementalGenerator
{
    private const string KeysetAttributeName = "Elarion.Paging.KeysetAttribute`1";
    private const string KeysetNamespace = "global::Elarion.Paging";
    private const string ProviderAttributeName = "Elarion.EntityFrameworkCore.UseElarionEntityFrameworkCoreAttribute";

    // ValueTuple.Create has direct overloads up to 7 elements; row-value seek is only emitted within
    // that range (and never for a single column, where a row value offers nothing over a scalar compare).
    private const int MaxRowValueColumns = 7;

    private static readonly DiagnosticDescriptor UnknownColumn = new(
        id: "ELKEY001",
        title: "Keyset column does not match a property",
        messageFormat: "Entity '{0}' has a [Keyset] column '{1}' that does not match any property; no keyset will be generated",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedColumnType = new(
        id: "ELKEY002",
        title: "Keyset column type is not supported",
        messageFormat: "Entity '{0}' has a [Keyset] column '{1}' of type '{2}', which is not supported for keyset pagination; no keyset will be generated",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NullableColumn = new(
        id: "ELKEY003",
        title: "Keyset column must be non-nullable",
        messageFormat: "Entity '{0}' has a nullable [Keyset] column '{1}'; keyset columns must be non-nullable for deterministic ordering; no keyset will be generated",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NonDeterministicKeyset = new(
        id: "ELKEY004",
        title: "Keyset may be non-deterministic",
        messageFormat: "Entity '{0}' has a property 'Id' that is not part of its [Keyset]; append a unique column such as the key so paging is deterministic",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidKeysetClass = new(
        id: "ELKEY005",
        title: "Keyset class must be a top-level partial class",
        messageFormat: "Keyset class '{0}' must be a non-nested partial class; the generator emits the keyset definition into it",
        category: "Elarion.EntityFrameworkCore",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                KeysetAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx));

        var provider = context.CompilationProvider
            .Select(static (compilation, _) => ReadProvider(compilation));

        context.RegisterSourceOutput(
            targets.Combine(provider),
            static (spc, pair) =>
            {
                var (target, provider) = pair;
                if (target is null)
                {
                    return;
                }

                foreach (var diagnostic in target.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }

                if (target.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    return;
                }

                Emit(spc, target, provider);
            });
    }

    private enum ProviderKind
    {
        Portable,
        Npgsql,
    }

    private static ProviderKind ReadProvider(Compilation compilation)
    {
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != ProviderAttributeName)
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key == "Provider")
                {
                    return ToProviderKind(argument.Value);
                }
            }
        }

        return ProviderKind.Portable;
    }

    private static ProviderKind ToProviderKind(TypedConstant value)
    {
        if (value.Kind != TypedConstantKind.Enum || value.Type is not INamedTypeSymbol enumType)
        {
            return ProviderKind.Portable;
        }

        // Resolve by member name so the contract is robust to enum reordering.
        var member = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, value.Value));

        return member?.Name == "Npgsql" ? ProviderKind.Npgsql : ProviderKind.Portable;
    }

    private static KeysetTarget? GetTarget(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol keysetClass)
        {
            return null;
        }

        // The keyset class carries the definition; the entity comes from the generic type argument.
        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].AttributeClass is not { TypeArguments.Length: 1 } attributeClass ||
            attributeClass.TypeArguments[0] is not INamedTypeSymbol entity)
        {
            return null;
        }

        var location = LocationModel.From(keysetClass);
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticModel>();

        // The generator completes the class as a partial; a nested or non-partial class cannot be filled.
        var isPartial = ctx.TargetNode is TypeDeclarationSyntax typeDecl &&
            typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
        if (!isPartial || keysetClass.ContainingType is not null)
        {
            diagnostics.Add(DiagnosticModel.Create(InvalidKeysetClass, location, keysetClass.Name));
        }

        var columns = ImmutableArray.CreateBuilder<ColumnModel>();
        var columnSpecs = ReadColumns(ctx.Attributes);
        if (!columnSpecs.IsEmpty)
        {
            var properties = GetAccessibleProperties(entity);
            var columnNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var spec in columnSpecs)
            {
                columnNames.Add(spec.Name);
                if (!properties.TryGetValue(spec.Name, out var property))
                {
                    diagnostics.Add(DiagnosticModel.Create(UnknownColumn, location, entity.Name, spec.Name));
                    continue;
                }

                if (IsNullable(property.Type))
                {
                    diagnostics.Add(DiagnosticModel.Create(NullableColumn, location, entity.Name, spec.Name));
                    continue;
                }

                var mapping = TypeMapping.For(property.Type);
                if (mapping is null)
                {
                    diagnostics.Add(DiagnosticModel.Create(
                        UnsupportedColumnType, location, entity.Name, spec.Name, property.Type.ToDisplayString()));
                    continue;
                }

                columns.Add(new ColumnModel(
                    spec.Name,
                    spec.Descending,
                    property.Type.ToDisplayString(FullyQualified),
                    mapping.Value.WriteMethod,
                    mapping.Value.ReadMethod,
                    mapping.Value.NeedsCast,
                    mapping.Value.EncodeCast,
                    mapping.Value.UseCompareTo));
            }

            if (properties.ContainsKey("Id") && !columnNames.Contains("Id"))
            {
                diagnostics.Add(DiagnosticModel.Create(NonDeterministicKeyset, location, entity.Name));
            }
        }

        return new KeysetTarget(
            keysetClass.Name,
            keysetClass.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : keysetClass.ContainingNamespace.ToDisplayString(),
            keysetClass.ToDisplayString(FullyQualified),
            entity.ToDisplayString(FullyQualified),
            columns.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<ColumnSpec> ReadColumns(ImmutableArray<AttributeData> attributes)
    {
        if (attributes.IsEmpty || attributes[0].ConstructorArguments.Length == 0)
        {
            return ImmutableArray<ColumnSpec>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<ColumnSpec>();
        foreach (var value in attributes[0].ConstructorArguments[0].Values)
        {
            if (value.Value is not string raw || raw.Length == 0)
            {
                continue;
            }

            var descending = raw[0] == '-';
            var name = raw[0] is '-' or '+' ? raw.Substring(1) : raw;
            if (name.Length > 0)
            {
                builder.Add(new ColumnSpec(name, descending));
            }
        }

        return builder.ToImmutable();
    }

    private static Dictionary<string, IPropertySymbol> GetAccessibleProperties(INamedTypeSymbol entity)
    {
        var result = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        for (var type = entity; type is not null; type = type.BaseType)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, GetMethod: not null } property &&
                    !result.ContainsKey(property.Name))
                {
                    result.Add(property.Name, property);
                }
            }
        }

        return result;
    }

    private static bool IsNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return true;
        }

        return type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;
    }

    private static void Emit(SourceProductionContext context, KeysetTarget target, ProviderKind provider)
    {
        if (target.Columns.IsEmpty)
        {
            return;
        }

        var entity = target.EntityGlobalFqn;
        var useRowValues = provider == ProviderKind.Npgsql && CanUseRowValues(target);
        var className = target.ClassName;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();

        var hasNamespace = target.ClassNamespace.Length > 0;
        if (hasNamespace)
        {
            sb.AppendLine($"namespace {target.ClassNamespace};");
            sb.AppendLine();
        }

        // Complete the user's partial keyset class as the definition; the entity is referenced by its
        // fully-qualified name, so it can live in any namespace and stays free of pagination attributes.
        sb.AppendLine($"partial class {className} : {KeysetNamespace}.IKeysetDefinition<{entity}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static {className} Definition {{ get; }} = new();");
        sb.AppendLine();
        sb.AppendLine($"    private {className}() {{ }}");
        sb.AppendLine();

        EmitApplyOrder(sb, target, entity);
        sb.AppendLine();
        EmitBuildSeek(sb, target, entity, useRowValues);
        sb.AppendLine();
        EmitToEntriesAsync(sb, target, entity);
        sb.AppendLine();
        EmitTryDecode(sb, target);
        sb.AppendLine();
        EmitProjectionType(sb, target);

        sb.AppendLine("}");

        // Hint name keys on the keyset class (not the entity) so an entity with multiple keysets emits
        // multiple distinct files.
        var hint = target.ClassGlobalFqn
            .Replace("global::", string.Empty)
            .Replace('.', '_')
            .Replace('+', '_');
        context.AddSource($"{hint}.Keyset.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitApplyOrder(StringBuilder sb, KeysetTarget target, string entity)
    {
        sb.AppendLine(
            $"    public global::System.Linq.IOrderedQueryable<{entity}> ApplyOrder(global::System.Linq.IQueryable<{entity}> source, bool forward)");
        sb.AppendLine($"        => forward");
        sb.AppendLine($"            ? source{BuildOrderChain(target, forward: true)}");
        sb.AppendLine($"            : source{BuildOrderChain(target, forward: false)};");
    }

    private static string BuildOrderChain(KeysetTarget target, bool forward)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < target.Columns.Length; i++)
        {
            var column = target.Columns[i];
            var descending = forward ? column.Descending : !column.Descending;
            var method = i == 0
                ? (descending ? "OrderByDescending" : "OrderBy")
                : (descending ? "ThenByDescending" : "ThenBy");
            sb.Append($".{method}(__e => __e.{column.PropertyName})");
        }

        return sb.ToString();
    }

    private static void EmitBuildSeek(StringBuilder sb, KeysetTarget target, string entity, bool useRowValues)
    {
        var outParams = string.Join(
            ", ",
            target.Columns.Select((c, i) => $"out var __key{i}"));

        sb.AppendLine(
            $"    public global::System.Linq.Expressions.Expression<global::System.Func<{entity}, bool>>? BuildSeek(string cursor, bool forward)");
        sb.AppendLine("    {");
        sb.AppendLine($"        if (!TryDecode(cursor, {outParams}))");
        sb.AppendLine("        {");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (forward)");
        sb.AppendLine("        {");
        sb.AppendLine($"            return {BuildSeekLambda(target, forward: true, useRowValues)};");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        return {BuildSeekLambda(target, forward: false, useRowValues)};");
        sb.AppendLine("    }");
    }

    private static string BuildSeekLambda(KeysetTarget target, bool forward, bool useRowValues)
    {
        if (useRowValues)
        {
            return BuildRowValueSeekLambda(target, forward);
        }

        var terms = new List<string>();
        for (var i = 0; i < target.Columns.Length; i++)
        {
            var parts = new List<string>();
            for (var j = 0; j < i; j++)
            {
                parts.Add($"__e.{target.Columns[j].PropertyName} == __key{j}");
            }

            parts.Add(BuildStrictComparison(target.Columns[i], i, forward));
            terms.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" && ", parts) + ")");
        }

        return "__e => " + string.Join(" || ", terms);
    }

    private static string BuildStrictComparison(ColumnModel column, int index, bool forward)
    {
        // Ascending columns seek "greater" when paging forward; descending columns seek "less".
        var useGreater = !column.Descending == forward;
        var op = useGreater ? ">" : "<";
        return column.UseCompareTo
            ? $"__e.{column.PropertyName}.CompareTo(__key{index}) {op} 0"
            : $"__e.{column.PropertyName} {op} __key{index}";
    }

    /// <summary>
    /// Whether the keyset can use a single PostgreSQL row-value comparison (Npgsql only): all columns
    /// must share one direction (row values compare lexicographically with a single operator) and the
    /// arity must fit <c>ValueTuple.Create</c>'s direct overloads. A single column gains nothing.
    /// </summary>
    private static bool CanUseRowValues(KeysetTarget target)
    {
        if (target.Columns.Length is < 2 or > MaxRowValueColumns)
        {
            return false;
        }

        var descending = target.Columns[0].Descending;
        foreach (var column in target.Columns)
        {
            if (column.Descending != descending)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildRowValueSeekLambda(KeysetTarget target, bool forward)
    {
        // Uniform direction is guaranteed by CanUseRowValues: ascending forward seeks greater,
        // descending forward seeks less. Npgsql translates this to "(a, b) > (@k0, @k1)" so a
        // composite index can satisfy the seek in one range scan; SQL handles Guid/string ordering,
        // so no CompareTo shim is needed.
        var useGreater = !target.Columns[0].Descending == forward;
        var method = useGreater ? "GreaterThan" : "LessThan";
        var columns = string.Join(", ", target.Columns.Select(c => $"__e.{c.PropertyName}"));
        var keys = string.Join(", ", target.Columns.Select((_, i) => $"__key{i}"));

        return "__e => global::Microsoft.EntityFrameworkCore.EF.Functions." + method +
            $"(global::System.ValueTuple.Create({columns}), global::System.ValueTuple.Create({keys}))";
    }

    private static void EmitToEntriesAsync(StringBuilder sb, KeysetTarget target, string entity)
    {
        sb.AppendLine(
            $"    public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<{KeysetNamespace}.KeysetEntry<TDto>>> ToEntriesAsync<TDto>(");
        sb.AppendLine($"        global::System.Linq.IQueryable<{entity}> query,");
        sb.AppendLine($"        global::System.Linq.Expressions.Expression<global::System.Func<{entity}, TDto>> selector,");
        sb.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        sb.AppendLine("    {");
        sb.AppendLine("        var __parameter = selector.Parameters[0];");
        // Compose the user projection with the key columns into one server-side projection so only the
        // selected and key columns are read; reusing selector's parameter keeps the tree single-parameter.
        sb.AppendLine(
            $"        var __projection = global::System.Linq.Expressions.Expression.Lambda<global::System.Func<{entity}, Projection<TDto>>>(");
        sb.AppendLine("            global::System.Linq.Expressions.Expression.New(");
        sb.AppendLine("                typeof(Projection<TDto>).GetConstructors()[0],");
        sb.AppendLine("                new global::System.Linq.Expressions.Expression[]");
        sb.AppendLine("                {");
        sb.AppendLine("                    selector.Body,");
        foreach (var column in target.Columns)
        {
            sb.AppendLine(
                $"                    global::System.Linq.Expressions.Expression.Property(__parameter, \"{column.PropertyName}\"),");
        }

        sb.AppendLine("                }),");
        sb.AppendLine("            __parameter);");
        sb.AppendLine();
        sb.AppendLine(
            "        var __rows = await global::Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(");
        sb.AppendLine(
            "            global::System.Linq.Queryable.Select(query, __projection), cancellationToken).ConfigureAwait(false);");
        sb.AppendLine();
        sb.AppendLine(
            $"        var __entries = new {KeysetNamespace}.KeysetEntry<TDto>[__rows.Count];");
        sb.AppendLine("        for (var __i = 0; __i < __rows.Count; __i++)");
        sb.AppendLine("        {");
        sb.AppendLine($"            var __writer = new {KeysetNamespace}.CursorWriter();");
        for (var i = 0; i < target.Columns.Length; i++)
        {
            var column = target.Columns[i];
            sb.AppendLine($"            __writer.{column.WriteMethod}({column.EncodeCast}__rows[__i].Key{i});");
        }

        sb.AppendLine(
            $"            __entries[__i] = new {KeysetNamespace}.KeysetEntry<TDto>(__rows[__i].Item, __writer.ToCursor());");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return __entries;");
        sb.AppendLine("    }");
    }

    private static void EmitProjectionType(StringBuilder sb, KeysetTarget target)
    {
        var ctorParameters = string.Join(
            ", ",
            new[] { "TDto item" }.Concat(
                target.Columns.Select((c, i) => $"{c.PropertyTypeGlobalFqn} key{i}")));

        sb.AppendLine("    private sealed class Projection<TDto>");
        sb.AppendLine("    {");
        sb.AppendLine($"        public Projection({ctorParameters})");
        sb.AppendLine("        {");
        sb.AppendLine("            Item = item;");
        for (var i = 0; i < target.Columns.Length; i++)
        {
            sb.AppendLine($"            Key{i} = key{i};");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public TDto Item { get; }");
        for (var i = 0; i < target.Columns.Length; i++)
        {
            sb.AppendLine($"        public {target.Columns[i].PropertyTypeGlobalFqn} Key{i} {{ get; }}");
        }

        sb.AppendLine("    }");
    }

    private static void EmitTryDecode(StringBuilder sb, KeysetTarget target)
    {
        var outParams = string.Join(
            ", ",
            target.Columns.Select((c, i) => $"out {c.PropertyTypeGlobalFqn} __key{i}"));

        sb.AppendLine($"    private static bool TryDecode(string cursor, {outParams})");
        sb.AppendLine("    {");
        for (var i = 0; i < target.Columns.Length; i++)
        {
            sb.AppendLine($"        __key{i} = default!;");
        }

        sb.AppendLine($"        if (!{KeysetNamespace}.CursorReader.TryCreate(cursor, out var reader))");
        sb.AppendLine("        {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine();

        for (var i = 0; i < target.Columns.Length; i++)
        {
            var column = target.Columns[i];
            if (column.NeedsCast)
            {
                sb.AppendLine($"        if (!reader.{column.ReadMethod}(out var __raw{i}))");
                sb.AppendLine("        {");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine($"        __key{i} = ({column.PropertyTypeGlobalFqn})__raw{i};");
            }
            else
            {
                sb.AppendLine($"        if (!reader.{column.ReadMethod}(out __key{i}))");
                sb.AppendLine("        {");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
            }

            sb.AppendLine();
        }

        sb.AppendLine("        return reader.AtEnd;");
        sb.AppendLine("    }");
    }

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly record struct ColumnSpec(string Name, bool Descending);

    private sealed record ColumnModel(
        string PropertyName,
        bool Descending,
        string PropertyTypeGlobalFqn,
        string WriteMethod,
        string ReadMethod,
        bool NeedsCast,
        string EncodeCast,
        bool UseCompareTo);

    private sealed record KeysetTarget(
        string ClassName,
        string ClassNamespace,
        string ClassGlobalFqn,
        string EntityGlobalFqn,
        EquatableArray<ColumnModel> Columns,
        EquatableArray<DiagnosticModel> Diagnostics);

    private sealed record DiagnosticModel(
        DiagnosticDescriptor Descriptor,
        LocationModel Location,
        EquatableArray<string> Args)
    {
        public DiagnosticSeverity Severity => Descriptor.DefaultSeverity;

        public static DiagnosticModel Create(DiagnosticDescriptor descriptor, LocationModel location, params string[] args)
            => new(descriptor, location, args.ToImmutableArray());

        public Diagnostic ToDiagnostic()
            => Diagnostic.Create(Descriptor, Location.ToLocation(), [.. Args]);
    }

    private readonly record struct LocationModel(string? FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public static LocationModel From(ISymbol symbol)
        {
            var location = symbol.Locations.FirstOrDefault();
            if (location is null || location.SourceTree is null)
            {
                return new LocationModel(null, default, default);
            }

            return new LocationModel(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
        }

        public Location? ToLocation()
            => FilePath is null ? null : Location.Create(FilePath, Span, LineSpan);
    }

    private readonly record struct TypeMapping(
        string WriteMethod,
        string ReadMethod,
        bool NeedsCast,
        string EncodeCast,
        bool UseCompareTo)
    {
        public static TypeMapping? For(ITypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
            {
                var underlying = enumType.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32;
                return underlying is SpecialType.System_Int64 or SpecialType.System_UInt64
                    ? new TypeMapping("WriteInt64", "TryReadInt64", true, "(long)", false)
                    : new TypeMapping("WriteInt32", "TryReadInt32", true, "(int)", false);
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Int32:
                    return new TypeMapping("WriteInt32", "TryReadInt32", false, string.Empty, false);
                case SpecialType.System_Int64:
                    return new TypeMapping("WriteInt64", "TryReadInt64", false, string.Empty, false);
                case SpecialType.System_Int16:
                    return new TypeMapping("WriteInt32", "TryReadInt32", true, "(int)", false);
                case SpecialType.System_Byte:
                    return new TypeMapping("WriteInt32", "TryReadInt32", true, "(int)", false);
                case SpecialType.System_Double:
                    return new TypeMapping("WriteDouble", "TryReadDouble", false, string.Empty, false);
                case SpecialType.System_Single:
                    return new TypeMapping("WriteSingle", "TryReadSingle", false, string.Empty, false);
                case SpecialType.System_Decimal:
                    return new TypeMapping("WriteDecimal", "TryReadDecimal", false, string.Empty, false);
                case SpecialType.System_String:
                    return new TypeMapping("WriteString", "TryReadString", false, string.Empty, true);
                case SpecialType.System_DateTime:
                    return new TypeMapping("WriteDateTime", "TryReadDateTime", false, string.Empty, false);
            }

            return type.ToDisplayString() switch
            {
                "System.Guid" => new TypeMapping("WriteGuid", "TryReadGuid", false, string.Empty, true),
                "System.DateTimeOffset" => new TypeMapping("WriteDateTimeOffset", "TryReadDateTimeOffset", false, string.Empty, false),
                "System.DateOnly" => new TypeMapping("WriteDateOnly", "TryReadDateOnly", false, string.Empty, false),
                "System.TimeOnly" => new TypeMapping("WriteTimeOnly", "TryReadTimeOnly", false, string.Empty, false),
                _ => null,
            };
        }
    }
}
