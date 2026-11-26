using System.Collections.Immutable;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Spreadsheets.Generators;

/// <summary>
/// Emits, per <c>[SpreadsheetRecord]</c> class (ADR-0108), the self-mapping half (<c>ISpreadsheetRecord&lt;T&gt;</c> and a
/// nested <c>Columns</c> class with one <c>SpreadsheetColumn&lt;T&gt;</c> per column) and a sealed
/// <c>{T}SpreadsheetRowMapper</c> with the static column table and the typed write code per column. There is no
/// reflection twin: a property the mapping cannot carry is a build error.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class SpreadsheetRecordGenerator : IIncrementalGenerator {
    private const string RecordAttributeName = "Elarion.Spreadsheets.SpreadsheetRecordAttribute";
    private const string ColumnAttributeName = "Elarion.Spreadsheets.SpreadsheetColumnAttribute";
    private const string IgnoreAttributeName = "Elarion.Spreadsheets.SpreadsheetIgnoreAttribute";
    private const string ChoiceAttributeName = "Elarion.Spreadsheets.SpreadsheetChoiceAttribute";
    private const string DisplayAttributeName = "System.ComponentModel.DataAnnotations.DisplayAttribute";
    private const string ValidationAttributeName = "System.ComponentModel.DataAnnotations.ValidationAttribute";
    private const string ValidatableObjectName = "System.ComponentModel.DataAnnotations.IValidatableObject";
    private const string Category = "Elarion.Spreadsheets";
    private const int MaxWorksheetNameLength = 31;

    private static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        "ELSHEET001",
        "Unsupported spreadsheet column type",
        "Property '{0}.{1}' of type '{2}' is not a supported spreadsheet column type; use string, bool, a number, "
        + "DateTime, DateTimeOffset, DateOnly, TimeOnly, Guid, an enum or their nullable forms, or exclude it with "
        + "[SpreadsheetIgnore]",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NotPartial = new(
        "ELSHEET002",
        "[SpreadsheetRecord] type must be partial",
        "[SpreadsheetRecord] type '{0}' must be declared 'partial' so the generated members "
        + "(ISpreadsheetRecord<T>.SpreadsheetMapper, Columns) can be added; add the 'partial' modifier",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateHeader = new(
        "ELSHEET003",
        "Duplicate spreadsheet header or alias",
        "[SpreadsheetRecord] type '{0}': the header or alias '{1}' of property '{2}' equals the header or alias '{3}' "
        + "of property '{4}' ignoring case; a file could not tell the columns apart",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidWorksheetName = new(
        "ELSHEET004",
        "Invalid worksheet name",
        "[SpreadsheetRecord] type '{0}': the worksheet name '{1}' is not valid: {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor BooleanLabelsMisused = new(
        "ELSHEET005",
        "Boolean labels misused",
        "Property '{0}.{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedTypeShape = new(
        "ELSHEET006",
        "Unsupported [SpreadsheetRecord] type shape",
        "[SpreadsheetRecord] type '{0}' must be a non-nested, non-generic, non-abstract class or record class; {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ReservedMemberName = new(
        "ELSHEET007",
        "[SpreadsheetRecord] type declares a reserved member name",
        "[SpreadsheetRecord] type '{0}' declares a member named '{1}', which collides with a generated member; rename it "
        + "(its column can keep its header through [SpreadsheetColumn])",
        Category,
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor AmbiguousChoice = new(
        "ELSHEET008",
        "Two enum members share a label, name or alias",
        "Property '{0}.{1}': the label, name or alias '{2}' of member '{3}.{4}' equals the label, name or alias '{5}' of "
        + "member '{3}.{6}' ignoring case; a file could not tell the members apart",
        Category,
        DiagnosticSeverity.Error,
        true);

    // The members the generated ISpreadsheetRecord<T> half adds to the record.
    private static readonly string[] ReservedMemberNames = ["Columns", "SpreadsheetMapper"];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var records = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RecordAttributeName,
                static (node, _) =>
                    node is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax,
                static (ctx, _) => GetModel(ctx))
            .Where(static model => model is not null)
            .WithTrackingName("SpreadsheetRecords");

        context.RegisterSourceOutput(records, static (spc, model) => {
            foreach (var diagnostic in model!.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (model.Emit) EmitMapper(spc, model);

            if (model.EmitSelfMapping) EmitSelfMapping(spc, model);
        });
    }

    /// <summary>How a column's value reaches the cell writer.</summary>
    private enum WriteKind {
        Text,
        Boolean,
        LabelledBoolean,
        Integer,
        UnsignedInteger,
        Single,
        Double,
        Decimal,
        DateTime,
        Date,
        Time,
        DateTimeOffset,
        Guid,
        Enum
    }

    // Aliases only read (FR-071): trimmed, without empty ones or ones equal to the member's own label or name.
    private sealed record EnumMemberModel(string Name, string Label, EquatableArray<string> Aliases);

    private sealed record ColumnModel(
        string PropertyName,
        string Header,
        // The public SpreadsheetValueKind member name.
        string Kind,
        WriteKind WriteKind,
        bool IsNullable,
        string? Format,
        double Width,
        EquatableArray<string> Aliases,
        // The enum type of an enum column; null otherwise.
        string? EnumTypeFqn,
        // An enum's members with distinct values, in declaration order; the labels of a labelled boolean.
        EquatableArray<EnumMemberModel> EnumMembers,
        EquatableArray<string> Choices,
        string? TrueLabel,
        string? FalseLabel,
        int Order,
        // The property's type without Nullable<>, and as declared, both fully qualified.
        string ValueTypeFqn,
        string PropertyTypeFqn,
        // Whether the generated mapper can set the property in an object initializer.
        bool CanSet);

    private sealed record SpreadsheetRecordModel(
        string Namespace,
        string TypeName,
        string TypeFqn,
        string MapperName,
        string HintName,
        string Accessibility,
        string TypeKeyword,
        string WorksheetName,
        bool Emit,
        bool EmitSelfMapping,
        EquatableArray<ColumnModel> Columns,
        EquatableArray<DiagnosticInfo> Diagnostics,
        bool HasValidation,
        // How ReadRow builds the record: the properties passed to a constructor, in parameter order (empty for the
        // parameterless one), or the reason it cannot build one.
        EquatableArray<string> ConstructorArguments,
        string? ConstructionProblem);

    private static SpreadsheetRecordModel? GetModel(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "";
        var typeName = type.Name;
        var typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var mapperName = typeName + "SpreadsheetRowMapper";
        var hintName = (ns.Length == 0 ? typeName : ns + "." + typeName).Replace('.', '_');
        var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var keyword = type.IsRecord ? "record" : "class";
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        string? explicitName = null;
        if (ctx.Attributes.Length > 0 && ctx.Attributes[0].ConstructorArguments.Length > 0)
            explicitName = ctx.Attributes[0].ConstructorArguments[0].Value as string;
        var worksheetName = explicitName ?? typeName;

        var shapeProblem = type.TypeKind == TypeKind.Struct ? "a struct cannot be a spreadsheet record"
            : type.TypeKind != TypeKind.Class ? "only a class can be a spreadsheet record"
            : type.ContainingType is not null ? "a nested type cannot be a spreadsheet record"
            : type.IsGenericType ? "a generic type cannot be a spreadsheet record"
            : type.IsAbstract ? "an abstract type cannot be a spreadsheet record"
            : type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
                ? "the type must be public or internal"
                : null;
        if (shapeProblem is not null) {
            diagnostics.Add(DiagnosticInfo.Create(UnsupportedTypeShape, LocationInfo.From(type), typeFqn, shapeProblem));
            return Invalid();
        }

        if (ValidateWorksheetName(worksheetName) is { } nameProblem)
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidWorksheetName, LocationInfo.From(type), typeFqn, worksheetName, nameProblem));

        var columns = CollectColumns(type, typeFqn, diagnostics);
        ReportDuplicateHeaders(type, typeFqn, columns, diagnostics);
        if (diagnostics.Count > 0) return Invalid();

        // The mapper still emits without the partial modifier or with a colliding member, so the one-keyword or rename
        // fix is the only error the author sees.
        var isPartial = IsDeclaredPartial(type);
        if (!isPartial) diagnostics.Add(DiagnosticInfo.Create(NotPartial, LocationInfo.From(type), typeFqn));

        var hasReservedCollision = false;
        foreach (var reserved in ReservedMemberNames)
            if (!type.GetMembers(reserved).IsEmpty) {
                hasReservedCollision = true;
                diagnostics.Add(DiagnosticInfo.Create(ReservedMemberName, LocationInfo.From(type), typeFqn, reserved));
            }

        var arranged = Arrange(columns);
        var constructorArguments = ChooseConstruction(type, arranged, out var constructionProblem);
        return new SpreadsheetRecordModel(
            ns, typeName, typeFqn, mapperName, hintName, accessibility, keyword, worksheetName,
            true, isPartial && !hasReservedCollision,
            arranged.ToEquatableArray(),
            diagnostics.ToImmutable().ToEquatableArray(),
            HasValidation(type),
            constructorArguments.ToEquatableArray(),
            constructionProblem);

        SpreadsheetRecordModel Invalid() {
            return new SpreadsheetRecordModel(
                ns, typeName, typeFqn, mapperName, hintName, accessibility, keyword, worksheetName,
                false, false, EquatableArray<ColumnModel>.Empty, diagnostics.ToImmutable().ToEquatableArray(),
                false, EquatableArray<string>.Empty, null);
        }
    }

    /// <summary>
    /// Columns without an order keep their declaration order; each ordered column then takes its position (in order of
    /// position, then declaration), so <c>Order = 1</c> makes a column the second.
    /// </summary>
    private static List<ColumnModel> Arrange(List<ColumnModel> columns) {
        var arranged = columns.Where(static column => column.Order < 0).ToList();
        foreach (var ordered in columns.Where(static column => column.Order >= 0).OrderBy(static column => column.Order))
            arranged.Insert(Math.Min(ordered.Order, arranged.Count), ordered);

        return arranged;
    }

    private static string? ValidateWorksheetName(string name) {
        if (name.Length == 0) return "it is empty";
        if (name.Length > MaxWorksheetNameLength) return $"it is longer than {MaxWorksheetNameLength} characters";

        foreach (var c in name)
            if (c is '[' or ']' or ':' or '*' or '?' or '/' or '\\')
                return $"it contains '{c}', which a worksheet name may not contain";

        if (name[0] == '\'' || name[name.Length - 1] == '\'') return "it starts or ends with an apostrophe";

        foreach (var c in name)
            if (char.IsControl(c))
                return "it contains a control character";

        return null;
    }

    private static bool IsDeclaredPartial(INamedTypeSymbol type) {
        foreach (var reference in type.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(static m => m.IsKind(SyntaxKind.PartialKeyword)))
                return true;

        return false;
    }

    /// <summary>Public instance properties with a public getter, base type first; an override replaces in place.</summary>
    private static List<ColumnModel> CollectColumns(
        INamedTypeSymbol type, string typeFqn, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            hierarchy.Insert(0, current);

        var columns = new List<ColumnModel>();
        var indexByProperty = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var declaring in hierarchy)
        foreach (var member in declaring.GetMembers()) {
            if (member is not IPropertySymbol property
                || property.IsStatic
                || property.IsIndexer
                || property.DeclaredAccessibility != Accessibility.Public
                || property.ExplicitInterfaceImplementations.Length > 0
                || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                continue;

            if (HasAttribute(property, IgnoreAttributeName)) {
                Remove(property.Name);
                continue;
            }

            var column = MapColumn(property, typeFqn, diagnostics);
            if (column is null) {
                Remove(property.Name);
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

        return columns;

        void Remove(string propertyName) {
            if (!indexByProperty.TryGetValue(propertyName, out var index)) return;

            columns.RemoveAt(index);
            indexByProperty.Remove(propertyName);
            foreach (var key in indexByProperty.Keys.ToList())
                if (indexByProperty[key] > index)
                    indexByProperty[key] -= 1;
        }
    }

    private static ColumnModel? MapColumn(
        IPropertySymbol property, string typeFqn, ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        string? header = null;
        var order = -1;
        string? format = null;
        var width = 0d;
        var aliases = ImmutableArray<string>.Empty;
        string? trueLabel = null;
        string? falseLabel = null;
        string? displayName = null;

        foreach (var attribute in property.GetAttributes())
            switch (attribute.AttributeClass?.ToDisplayString()) {
                case ColumnAttributeName:
                    if (attribute.ConstructorArguments.Length > 0)
                        header = attribute.ConstructorArguments[0].Value as string;

                    foreach (var argument in attribute.NamedArguments)
                        switch (argument.Key) {
                            case "Order" when argument.Value.Value is int value:
                                order = value;
                                break;
                            case "Format":
                                format = argument.Value.Value as string;
                                break;
                            case "Width" when argument.Value.Value is double value:
                                width = value;
                                break;
                            case "Aliases" when !argument.Value.IsNull:
                                aliases = [
                                    .. argument.Value.Values
                                        .Select(static value => value.Value as string)
                                        .Where(static value => value is not null)
                                        .Select(static value => value!)
                                ];
                                break;
                            case "TrueLabel":
                                trueLabel = argument.Value.Value as string;
                                break;
                            case "FalseLabel":
                                falseLabel = argument.Value.Value as string;
                                break;
                        }

                    break;
                case DisplayAttributeName:
                    displayName = DisplayName(attribute);
                    break;
            }

        header ??= displayName ?? property.Name;

        var propertyType = property.Type;
        var effective = propertyType;
        bool isNullable;
        if (propertyType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) {
            effective = nullable.TypeArguments[0];
            isNullable = true;
        }
        else {
            // An unannotated (nullable-oblivious) reference type counts as nullable.
            isNullable = !propertyType.IsValueType && propertyType.NullableAnnotation != NullableAnnotation.NotAnnotated;
        }

        var canSet = !property.IsReadOnly
                     && property.SetMethod is {
                         DeclaredAccessibility: Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
                     };
        var valueTypeFqn = effective.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var propertyTypeFqn = propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var hasLabels = trueLabel is not null || falseLabel is not null;
        var isBoolean = effective.SpecialType == SpecialType.System_Boolean;
        if (hasLabels) {
            var problem = !isBoolean
                ? "TrueLabel and FalseLabel apply only to a bool property"
                : trueLabel is null || falseLabel is null
                    ? "a boolean column needs both TrueLabel and FalseLabel"
                    : trueLabel.Trim().Length == 0 || falseLabel.Trim().Length == 0
                        ? "TrueLabel and FalseLabel must not be empty"
                        : string.Equals(trueLabel.Trim(), falseLabel.Trim(), StringComparison.OrdinalIgnoreCase)
                            ? "TrueLabel and FalseLabel must differ ignoring case"
                            : null;
            if (problem is not null) {
                diagnostics.Add(DiagnosticInfo.Create(
                    BooleanLabelsMisused, LocationInfo.From(property), typeFqn, property.Name, problem));
                return null;
            }
        }

        if (effective.TypeKind == TypeKind.Enum && effective is INamedTypeSymbol enumType) {
            var members = EnumMembers(enumType);
            if (ReportAmbiguousChoices(property, typeFqn, enumType, members, diagnostics)) return null;

            return new ColumnModel(
                property.Name, header, "Choice", WriteKind.Enum, isNullable, format, width,
                aliases.ToEquatableArray(), enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                members.ToEquatableArray(), members.Select(static member => member.Label).ToEquatableArray(),
                null, null, order, valueTypeFqn, propertyTypeFqn, canSet);
        }

        if (isBoolean && hasLabels)
            return new ColumnModel(
                property.Name, header, "Choice", WriteKind.LabelledBoolean, isNullable, format, width,
                aliases.ToEquatableArray(), null, EquatableArray<EnumMemberModel>.Empty,
                ImmutableArray.Create(trueLabel!, falseLabel!).ToEquatableArray(), trueLabel, falseLabel, order,
                valueTypeFqn, propertyTypeFqn, canSet);

        var mapped = MapScalar(effective);
        if (mapped is null) {
            diagnostics.Add(DiagnosticInfo.Create(
                UnsupportedPropertyType, LocationInfo.From(property),
                typeFqn, property.Name, propertyType.ToDisplayString()));
            return null;
        }

        return new ColumnModel(
            property.Name, header, mapped.Value.Kind, mapped.Value.WriteKind, isNullable, format, width,
            aliases.ToEquatableArray(), null, EquatableArray<EnumMemberModel>.Empty, EquatableArray<string>.Empty,
            null, null, order, valueTypeFqn, propertyTypeFqn, canSet);
    }

    private static (string Kind, WriteKind WriteKind)? MapScalar(ITypeSymbol type) {
        switch (type.SpecialType) {
            case SpecialType.System_String:
                return ("Text", WriteKind.Text);
            case SpecialType.System_Boolean:
                return ("Boolean", WriteKind.Boolean);
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
                return ("Integer", WriteKind.Integer);
            case SpecialType.System_UInt64:
                return ("Integer", WriteKind.UnsignedInteger);
            case SpecialType.System_Single:
                return ("Number", WriteKind.Single);
            case SpecialType.System_Double:
                return ("Number", WriteKind.Double);
            case SpecialType.System_Decimal:
                return ("Number", WriteKind.Decimal);
            case SpecialType.System_DateTime:
                return ("DateTime", WriteKind.DateTime);
        }

        return type.ToDisplayString() switch {
            "System.DateOnly" => ("Date", WriteKind.Date),
            "System.TimeOnly" => ("Time", WriteKind.Time),
            "System.DateTimeOffset" => ("DateTimeOffset", WriteKind.DateTimeOffset),
            "System.Guid" => ("Guid", WriteKind.Guid),
            _ => null
        };
    }

    /// <summary>
    /// An enum's members with distinct values, in declaration order, labelled by <c>[Display(Name)]</c>, with the aliases
    /// of <c>[SpreadsheetChoice]</c>.
    /// </summary>
    private static List<EnumMemberModel> EnumMembers(INamedTypeSymbol enumType) {
        var members = new List<EnumMemberModel>();
        var seen = new HashSet<object>();
        foreach (var member in enumType.GetMembers()) {
            if (member is not IFieldSymbol { HasConstantValue: true } field || field.ConstantValue is null) continue;
            if (!seen.Add(field.ConstantValue)) continue;

            string? label = null;
            var aliases = new List<string>();
            foreach (var attribute in field.GetAttributes())
                switch (attribute.AttributeClass?.ToDisplayString()) {
                    case DisplayAttributeName:
                        label = DisplayName(attribute);
                        break;
                    case ChoiceAttributeName:
                        foreach (var argument in attribute.NamedArguments)
                            if (argument is { Key: "Aliases", Value.IsNull: false })
                                aliases.AddRange(argument.Value.Values
                                    .Select(static value => (value.Value as string)?.Trim())
                                    .Where(static value => !string.IsNullOrEmpty(value))
                                    .Select(static value => value!));

                        break;
                }

            label ??= field.Name;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { label.Trim(), field.Name };
            members.Add(new EnumMemberModel(
                field.Name, label, aliases.Where(own.Add).ToImmutableArray().ToEquatableArray()));
        }

        return members;
    }

    /// <summary>
    /// Reports <c>ELSHEET008</c> for every label, name or alias of a member that equals one of an earlier member ignoring
    /// case (FR-072); returns whether it reported any.
    /// </summary>
    private static bool ReportAmbiguousChoices(
        IPropertySymbol property, string typeFqn, INamedTypeSymbol enumType, List<EnumMemberModel> members,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var enumName = enumType.ToDisplayString();
        var seen = new Dictionary<string, (string Text, string Member)>(StringComparer.OrdinalIgnoreCase);
        // One diagnostic per pair of members, however many of their spellings collide.
        var reportedPairs = new HashSet<(string, string)>();
        foreach (var member in members)
        foreach (var text in new[] { member.Label.Trim(), member.Name }.Concat(member.Aliases)) {
            if (seen.TryGetValue(text, out var first)) {
                if (first.Member != member.Name && reportedPairs.Add((first.Member, member.Name)))
                    diagnostics.Add(DiagnosticInfo.Create(
                        AmbiguousChoice, LocationInfo.From(property),
                        typeFqn, property.Name, text, enumName, member.Name, first.Text, first.Member));

                continue;
            }

            seen.Add(text, (text, member.Name));
        }

        return reportedPairs.Count > 0;
    }

    private static string? DisplayName(AttributeData attribute) {
        foreach (var argument in attribute.NamedArguments)
            if (argument.Key == "Name" && argument.Value.Value is string name)
                return name;

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeName) {
        foreach (var attribute in symbol.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == attributeName)
                return true;

        return false;
    }

    /// <summary>
    /// Whether the record's validator would check anything: a DataAnnotations attribute on the type or on one of its
    /// public instance properties (mapped or not, base types included), or <c>IValidatableObject</c>.
    /// </summary>
    private static bool HasValidation(INamedTypeSymbol type) {
        if (type.AllInterfaces.Any(static candidate => candidate.ToDisplayString() == ValidatableObjectName)) return true;

        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType) {
            if (current.GetAttributes().Any(IsValidationAttribute)) return true;

            foreach (var member in current.GetMembers())
                if (member is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } property
                    && property.GetAttributes().Any(IsValidationAttribute))
                    return true;
        }

        return false;
    }

    private static bool IsValidationAttribute(AttributeData attribute) {
        for (var current = attribute.AttributeClass; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == ValidationAttributeName)
                return true;

        return false;
    }

    /// <summary>
    /// How the generated reader builds a record: with an object initializer after the parameterless constructor (an
    /// empty list), or through the constructor whose parameters all name columns of the same type, such as a positional
    /// record's (their property names in parameter order). Every <c>required</c> member must be a column the reader
    /// sets; otherwise <paramref name="problem"/> says why the record cannot be read.
    /// </summary>
    private static List<string> ChooseConstruction(INamedTypeSymbol type, List<ColumnModel> columns, out string? problem) {
        problem = null;
        var accessible = type.InstanceConstructors
            .Where(static constructor => constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal)
            .ToList();
        var arguments = new List<string>();
        if (!accessible.Any(static constructor => constructor.Parameters.Length == 0)) {
            var match = accessible
                .Where(constructor => constructor.Parameters.All(parameter => columns.Any(column =>
                    string.Equals(column.PropertyName, parameter.Name, StringComparison.OrdinalIgnoreCase)
                    && column.PropertyTypeFqn == parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))))
                .OrderByDescending(static constructor => constructor.Parameters.Length)
                .FirstOrDefault();
            if (match is null) {
                problem = "it has no parameterless constructor and no constructor whose parameters all name its columns";
                return arguments;
            }

            arguments.AddRange(match.Parameters.Select(parameter => columns.First(column =>
                string.Equals(column.PropertyName, parameter.Name, StringComparison.OrdinalIgnoreCase)).PropertyName));
        }

        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            foreach (var member in current.GetMembers()) {
                var isRequired = member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };
                if (!isRequired || arguments.Contains(member.Name)) continue;
                if (columns.Any(column => column.PropertyName == member.Name && column.CanSet)) continue;

                problem = $"its required member '{member.Name}' is not a column the reader sets";
                return arguments;
            }

        return arguments;
    }

    private static void ReportDuplicateHeaders(
        INamedTypeSymbol type, string typeFqn, List<ColumnModel> columns,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var seen = new Dictionary<string, (string Name, string Property)>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        foreach (var name in new[] { column.Header }.Concat(column.Aliases)) {
            var key = name.Trim();
            if (seen.TryGetValue(key, out var first)) {
                if (first.Property != column.PropertyName)
                    diagnostics.Add(DiagnosticInfo.Create(
                        DuplicateHeader, LocationInfo.From(type),
                        typeFqn, name, column.PropertyName, first.Name, first.Property));

                continue;
            }

            seen.Add(key, (name, column.PropertyName));
        }
    }
}
