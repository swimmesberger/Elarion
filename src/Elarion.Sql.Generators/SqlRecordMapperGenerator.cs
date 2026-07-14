using System.Collections.Immutable;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Sql.Generators;

/// <summary>
/// Emits a sealed <c>{Type}SqlMapper : ISqlRowMapper&lt;T&gt;</c> per <c>[SqlRecord]</c> type
/// (ADR-0058): an <c>Ordinals</c> struct resolving column ordinals by name once per result set,
/// synchronous typed <c>GetFieldValue&lt;T&gt;</c> row reads (no boxing, no per-column await), typed
/// parameter binding, <c>TableName</c>/column constants, a static <c>Instance</c>, and one
/// per-assembly DI registration extension. There is deliberately no reflection twin: an unmapped type
/// is a compile error, never a silent runtime fallback.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class SqlRecordMapperGenerator : IIncrementalGenerator {
    private const string SqlRecordAttributeName = "Elarion.Sql.SqlRecordAttribute";
    private const string SqlColumnAttributeName = "Elarion.Sql.SqlColumnAttribute";
    private const string SqlIgnoreAttributeName = "Elarion.Sql.SqlIgnoreAttribute";
    private const string SqlJsonAttributeName = "Elarion.Sql.SqlJsonAttribute";
    private const string ProviderAttributeName = "Elarion.Sql.UseElarionSqlAttribute";

    private static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        "ELSQL001",
        "Unsupported SQL column type",
        "Property '{0}.{1}' of type '{2}' is not a supported SQL column type; use a supported primitive, "
        + "serialize it as JSON with [SqlJson], or exclude it with [SqlIgnore]",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateColumnName = new(
        "ELSQL002",
        "Duplicate column name",
        "[SqlRecord] type '{0}' maps properties '{1}' and '{2}' to the same column name '{3}'",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor RequiredPropertyNotMapped = new(
        "ELSQL003",
        "Required property is excluded from mapping",
        "Required property '{0}.{1}' is excluded from mapping; the generated mapper could not construct "
        + "the row — map it or drop 'required'",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoMappedColumns = new(
        "ELSQL004",
        "[SqlRecord] type has no mapped columns",
        "[SqlRecord] type '{0}' has no mapped columns; a row type needs at least one readable, writable "
        + "property of a supported type",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedTypeShape = new(
        "ELSQL005",
        "Unsupported [SqlRecord] type shape",
        "[SqlRecord] type '{0}' must be a non-nested, non-generic, non-abstract class, record, or struct",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoUsableConstructor = new(
        "ELSQL006",
        "No usable constructor",
        "[SqlRecord] type '{0}' has no usable constructor: it needs an accessible parameterless constructor "
        + "with writable (set/init) mapped properties, or a constructor whose parameters match mapped "
        + "columns by name (a positional record's primary constructor qualifies)",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ColumnNotWritable = new(
        "ELSQL007",
        "Annotated property is not writable",
        "Property '{0}.{1}' carries a mapping attribute but has no accessible setter or initializer",
        "Elarion.Sql",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var records = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SqlRecordAttributeName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax,
                static (ctx, _) => GetModel(ctx))
            .Where(static model => model is not null)
            .WithTrackingName("SqlRecords");

        // A compilation-derived scalar (value-equatable enum), so downstream nodes stay cached across
        // edits that do not change the assembly attribute (the KeysetGenerator provider precedent).
        var provider = context.CompilationProvider
            .Select(static (compilation, _) => ReadProvider(compilation));

        context.RegisterSourceOutput(records.Combine(provider), static (spc, pair) => {
            var model = pair.Left!;
            foreach (var diagnostic in model.Diagnostics) {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (model.Emit) {
                EmitMapper(spc, model, pair.Right);
            }
        });

        var registration = records.Collect().WithTrackingName("SqlMapperRegistration");
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "");
        context.RegisterSourceOutput(registration.Combine(assemblyName), static (spc, pair) =>
            EmitRegistration(spc, pair.Left, pair.Right));
    }

    private enum SqlProviderKind {
        Portable,
        Npgsql,
    }

    private enum ColumnKind {
        Scalar,
        Enum,
        Json,
    }

    private sealed record ColumnModel(
        string PropertyName,
        string ColumnName,
        ColumnKind Kind,
        // The T of reader.GetFieldValue<T> — the enum's underlying keyword, "string" for JSON columns.
        string ReadTypeFqn,
        // The enum type to cast a read value to; null for non-enum columns.
        string? EnumTypeFqn,
        // The type handed to IElarionJsonSerialization.GetTypeInfo<T> (non-nullable form); null for non-JSON columns.
        string? JsonTypeFqn,
        bool IsNullable,
        bool IsValueType,
        // Position of the matching primary-constructor parameter; -1 = set via object initializer.
        int CtorPosition);

    private sealed record SqlRecordModel(
        string Namespace,
        string TypeName,
        string TypeFqn,
        string MapperName,
        string HintName,
        string Accessibility,
        string TableName,
        bool Emit,
        bool RequiresJson,
        EquatableArray<ColumnModel> Columns,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static SqlProviderKind ReadProvider(Compilation compilation) {
        foreach (var attribute in compilation.Assembly.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != ProviderAttributeName) {
                continue;
            }

            foreach (var argument in attribute.NamedArguments) {
                if (argument.Key == "Provider" && argument.Value.Value is int value && value == 1) {
                    return SqlProviderKind.Npgsql;
                }
            }
        }

        return SqlProviderKind.Portable;
    }

    private static SqlRecordModel? GetModel(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) {
            return null;
        }

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "";
        var typeName = type.Name;
        var typeFqn = type.ToDisplayString(fmt);
        var mapperName = typeName + "SqlMapper";
        var hintName = (ns.Length == 0 ? typeName : ns + "." + typeName).Replace('.', '_');
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        string? tableName = null;
        if (ctx.Attributes.Length > 0 && ctx.Attributes[0].ConstructorArguments.Length > 0
            && ctx.Attributes[0].ConstructorArguments[0].Value is string explicitTable) {
            tableName = explicitTable;
        }

        var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        if (type.ContainingType is not null || type.IsGenericType || type.IsAbstract
            || type.TypeKind is not (TypeKind.Class or TypeKind.Struct)
            || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) {
            diagnostics.Add(DiagnosticInfo.Create(UnsupportedTypeShape, LocationInfo.From(type), typeFqn));
            return Invalid();
        }

        var columns = CollectColumns(type, diagnostics);
        if (columns.Count == 0 && diagnostics.Count == 0) {
            diagnostics.Add(DiagnosticInfo.Create(NoMappedColumns, LocationInfo.From(type), typeFqn));
        }

        if (diagnostics.Count > 0) {
            return Invalid();
        }

        var bound = BindConstructor(type, columns, diagnostics);
        if (bound is null) {
            return Invalid();
        }

        return new SqlRecordModel(
            ns,
            typeName,
            typeFqn,
            mapperName,
            hintName,
            accessibility,
            tableName ?? ToSnakeCase(typeName),
            Emit: true,
            RequiresJson: bound.Any(static column => column.Kind == ColumnKind.Json),
            bound.ToEquatableArray(),
            diagnostics.ToImmutable().ToEquatableArray());

        SqlRecordModel Invalid() => new(
            ns, typeName, typeFqn, mapperName, hintName, accessibility, tableName ?? ToSnakeCase(typeName),
            Emit: false, RequiresJson: false, EquatableArray<ColumnModel>.Empty,
            diagnostics.ToImmutable().ToEquatableArray());
    }

    /// <summary>Candidate properties in declaration order, base types first; overrides replace in place.</summary>
    private static List<ColumnModel> CollectColumns(
        INamedTypeSymbol type, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType) {
            hierarchy.Insert(0, current);
        }

        var columns = new List<ColumnModel>();
        var indexByProperty = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        foreach (var declaring in hierarchy) {
            foreach (var member in declaring.GetMembers()) {
                if (member is not IPropertySymbol property
                    || property.IsStatic
                    || property.IsIndexer
                    || property.ExplicitInterfaceImplementations.Length > 0
                    || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal }) {
                    continue;
                }

                var isIgnored = false;
                var isJson = false;
                string? explicitColumn = null;
                var hasMappingAttribute = false;
                foreach (var attribute in property.GetAttributes()) {
                    switch (attribute.AttributeClass?.ToDisplayString()) {
                        case SqlIgnoreAttributeName:
                            isIgnored = true;
                            break;
                        case SqlJsonAttributeName:
                            isJson = true;
                            hasMappingAttribute = true;
                            break;
                        case SqlColumnAttributeName:
                            hasMappingAttribute = true;
                            if (attribute.ConstructorArguments.Length > 0
                                && attribute.ConstructorArguments[0].Value is string name) {
                                explicitColumn = name;
                            }

                            break;
                    }
                }

                if (isIgnored) {
                    if (property.IsRequired) {
                        diagnostics.Add(DiagnosticInfo.Create(
                            RequiredPropertyNotMapped, LocationInfo.From(property), typeFqn, property.Name));
                    }

                    RemoveOverridden(property.Name);
                    continue;
                }

                var isWritable = property.SetMethod is
                    { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal };
                if (!isWritable && !HasMatchingConstructorParameter(type, property)) {
                    // Derived members (computed flags per the state-record convention) skip silently —
                    // unless the author explicitly asked for mapping.
                    if (hasMappingAttribute) {
                        diagnostics.Add(DiagnosticInfo.Create(
                            ColumnNotWritable, LocationInfo.From(property), typeFqn, property.Name));
                    }
                    else if (property.IsRequired) {
                        diagnostics.Add(DiagnosticInfo.Create(
                            RequiredPropertyNotMapped, LocationInfo.From(property), typeFqn, property.Name));
                    }

                    RemoveOverridden(property.Name);
                    continue;
                }

                var column = MapColumn(property, explicitColumn, isJson, typeFqn, diagnostics);
                if (column is null) {
                    RemoveOverridden(property.Name);
                    continue;
                }

                if (indexByProperty.TryGetValue(property.Name, out var existing)) {
                    columns[existing] = column;
                }
                else {
                    indexByProperty.Add(property.Name, columns.Count);
                    columns.Add(column);
                }
            }
        }

        // Duplicate column names across distinct properties fail loud (silent last-wins would corrupt reads).
        for (var i = 0; i < columns.Count; i++) {
            for (var j = i + 1; j < columns.Count; j++) {
                if (string.Equals(columns[i].ColumnName, columns[j].ColumnName, StringComparison.Ordinal)) {
                    diagnostics.Add(DiagnosticInfo.Create(
                        DuplicateColumnName, LocationInfo.From(type),
                        typeFqn, columns[i].PropertyName, columns[j].PropertyName, columns[i].ColumnName));
                }
            }
        }

        return columns;

        void RemoveOverridden(string propertyName) {
            // An override in a derived type re-evaluates the member; if it is now excluded, drop the
            // base entry so base and derived agree.
            if (indexByProperty.TryGetValue(propertyName, out var index)) {
                columns.RemoveAt(index);
                indexByProperty.Remove(propertyName);
                foreach (var key in indexByProperty.Keys.ToList()) {
                    if (indexByProperty[key] > index) {
                        indexByProperty[key] -= 1;
                    }
                }
            }
        }
    }

    private static bool HasMatchingConstructorParameter(INamedTypeSymbol type, IPropertySymbol property) {
        foreach (var constructor in type.InstanceConstructors) {
            foreach (var parameter in constructor.Parameters) {
                if (string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)
                    && SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static ColumnModel? MapColumn(
        IPropertySymbol property, string? explicitColumn, bool isJson, string typeFqn,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var propertyType = property.Type;
        var isNullable = false;
        var effective = propertyType;

        if (propertyType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) {
            effective = nullable.TypeArguments[0];
            isNullable = true;
        }
        else if (!propertyType.IsValueType) {
            // Unannotated (nullable-oblivious) reference types read defensively as nullable.
            isNullable = propertyType.NullableAnnotation != NullableAnnotation.NotAnnotated;
        }

        var columnName = explicitColumn ?? ToSnakeCase(property.Name);
        var isValueType = effective.IsValueType;

        if (isJson) {
            var jsonType = effective.ToDisplayString(fmt);
            return new ColumnModel(
                property.Name, columnName, ColumnKind.Json,
                ReadTypeFqn: "string", EnumTypeFqn: null, JsonTypeFqn: jsonType,
                isNullable, isValueType, CtorPosition: -1);
        }

        if (effective.TypeKind == TypeKind.Enum && effective is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
            var underlyingRead = TryMapScalar(underlying);
            if (underlyingRead is not null) {
                return new ColumnModel(
                    property.Name, columnName, ColumnKind.Enum,
                    ReadTypeFqn: underlyingRead, EnumTypeFqn: effective.ToDisplayString(fmt), JsonTypeFqn: null,
                    isNullable, isValueType, CtorPosition: -1);
            }
        }

        var readType = TryMapScalar(effective);
        if (readType is null) {
            diagnostics.Add(DiagnosticInfo.Create(
                UnsupportedPropertyType, LocationInfo.From(property),
                typeFqn, property.Name, property.Type.ToDisplayString()));
            return null;
        }

        return new ColumnModel(
            property.Name, columnName, ColumnKind.Scalar,
            ReadTypeFqn: readType, EnumTypeFqn: null, JsonTypeFqn: null,
            isNullable, isValueType, CtorPosition: -1);
    }

    private static string? TryMapScalar(ITypeSymbol type) {
        switch (type.SpecialType) {
            case SpecialType.System_String:
                return "string";
            case SpecialType.System_Boolean:
                return "bool";
            case SpecialType.System_Byte:
                return "byte";
            case SpecialType.System_Int16:
                return "short";
            case SpecialType.System_Int32:
                return "int";
            case SpecialType.System_Int64:
                return "long";
            case SpecialType.System_Single:
                return "float";
            case SpecialType.System_Double:
                return "double";
            case SpecialType.System_Decimal:
                return "decimal";
            case SpecialType.System_Char:
                return "char";
            case SpecialType.System_DateTime:
                return "global::System.DateTime";
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte }) {
            return "byte[]";
        }

        return type.ToDisplayString() switch {
            "System.Guid" => "global::System.Guid",
            "System.DateTimeOffset" => "global::System.DateTimeOffset",
            "System.DateOnly" => "global::System.DateOnly",
            "System.TimeOnly" => "global::System.TimeOnly",
            "System.TimeSpan" => "global::System.TimeSpan",
            _ => null,
        };
    }

    /// <summary>
    /// Chooses the construction strategy: object initializer when a parameterless constructor exists and
    /// every mapped property is writable, else the constructor whose parameters all match mapped columns
    /// by name (positional records). Returns null (and a diagnostic) when neither applies.
    /// </summary>
    private static List<ColumnModel>? BindConstructor(
        INamedTypeSymbol type, List<ColumnModel> columns, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var writable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var column in columns) {
            writable[column.PropertyName] = IsWritable(type, column.PropertyName);
        }

        var accessibleCtors = type.InstanceConstructors
            .Where(static ctor => ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .ToList();

        var hasParameterless = accessibleCtors.Any(static ctor => ctor.Parameters.Length == 0);
        if (hasParameterless && columns.All(column => writable[column.PropertyName])) {
            return columns;
        }

        // Positional path: every constructor parameter must match a mapped column (name,
        // case-insensitive + assignable type); prefer the widest such constructor.
        foreach (var ctor in accessibleCtors.OrderByDescending(static ctor => ctor.Parameters.Length)) {
            if (ctor.Parameters.Length == 0) {
                continue;
            }

            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var matched = true;
            for (var i = 0; i < ctor.Parameters.Length; i++) {
                var parameter = ctor.Parameters[i];
                var column = columns.FirstOrDefault(c =>
                    string.Equals(c.PropertyName, parameter.Name, StringComparison.OrdinalIgnoreCase));
                var property = column is null ? null : FindProperty(type, column.PropertyName);
                if (column is null || property is null
                    || !SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)) {
                    matched = false;
                    break;
                }

                positions[column.PropertyName] = i;
            }

            if (!matched) {
                continue;
            }

            // Columns not covered by the constructor still need a writable property.
            if (columns.Any(column => !positions.ContainsKey(column.PropertyName) && !writable[column.PropertyName])) {
                continue;
            }

            return [.. columns.Select(column =>
                positions.TryGetValue(column.PropertyName, out var position)
                    ? column with { CtorPosition = position }
                    : column)];
        }

        diagnostics.Add(DiagnosticInfo.Create(
            NoUsableConstructor, LocationInfo.From(type), type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return null;

        static bool IsWritable(INamedTypeSymbol type, string propertyName) =>
            FindProperty(type, propertyName)?.SetMethod is
                { DeclaredAccessibility: Accessibility.Public or Accessibility.Internal };

        static IPropertySymbol? FindProperty(INamedTypeSymbol type, string propertyName) {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object;
                 current = current.BaseType) {
                foreach (var member in current.GetMembers(propertyName)) {
                    if (member is IPropertySymbol property) {
                        return property;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>PascalCase → snake_case, System.Text.Json <c>SnakeCaseLower</c>-style ("OrderID" → "order_id").</summary>
    private static string ToSnakeCase(string name) {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++) {
            var c = name[i];
            if (char.IsUpper(c)) {
                var boundary = i > 0 && (
                    char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (boundary) {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
