using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// The declarations the JSON-RPC schema exports as values (ADR-0100):
/// <list type="bullet">
/// <item><c>[ClientConstants]</c> classes: their public constants are registered, per module, into the module's default
/// services as a <c>ModuleClientConstants</c>, which the schema export writes as its <c>constants</c> block — so a
/// constant exists exactly when its module is enabled, and nothing is scanned at run time. An unexportable member is
/// a build error.</item>
/// <item><c>[RequestPreset]</c> fields: checked here (a static readonly field of a JSON-RPC handler, of its request
/// type, with a unique name); the build-time schema export reads their values from the handler type.</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ClientExportsGenerator : IIncrementalGenerator {
    private const string ClientConstantsAttributeMetadataName = "Elarion.Abstractions.ClientConstantsAttribute";
    private const string RequestPresetAttributeMetadataName = "Elarion.Abstractions.RequestPresetAttribute";
    private const string ConstantsFqn = "global::Elarion.Abstractions.Modules.ModuleClientConstants";
    private const string ConstantFqn = "global::Elarion.Abstractions.Modules.ClientConstant";

    internal static readonly DiagnosticDescriptor InvalidPreset = new(
        "ELRPC006",
        "Invalid request preset",
        "[RequestPreset] field '{0}' is not exported because {1}; a preset is a static readonly field of a [Handler] "
        + "class holding that handler's request type",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor DuplicatePreset = new(
        "ELRPC007",
        "Duplicate request preset name",
        "Handler '{0}' declares the request preset '{1}' more than once; a preset name is unique per handler",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor PresetWithoutMethod = new(
        "ELRPC008",
        "Request preset of a handler that is not on JSON-RPC",
        "[RequestPreset] field '{0}' belongs to handler '{1}', which is not a JSON-RPC method, so the preset has no method "
        + "to belong to; expose the handler on JSON-RPC or remove the preset",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor InvalidConstantsClass = new(
        "ELRPC009",
        "Invalid client constants class",
        "[ClientConstants] class '{0}' exports nothing because {1}",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor UnexportableConstant = new(
        "ELRPC010",
        "Client constant cannot be exported",
        "Member '{0}' of [ClientConstants] class '{1}' cannot be exported because {2}; export public const or static "
        + "readonly members (or static get-only properties) of a number, string, boolean or enum type, or an array, "
        + "read-only list or immutable array of one, or make the member non-public",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor DuplicateConstant = new(
        "ELRPC011",
        "Duplicate client constant name",
        "Module '{0}' exports the client constant '{1}' from both '{2}' and '{3}'; a constant name is unique per module",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    /// <summary>One <c>[ClientConstants]</c> class: its exportable members, or why it exports nothing.</summary>
    private sealed record ConstantsClass(
        string Namespace,
        string TypeFqn,
        string Display,
        string? Problem,
        LocationInfo Location,
        EquatableArray<ConstantMember> Members,
        EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>One exported member: the exported name, the member access, its type, and where it is declared.</summary>
    private sealed record ConstantMember(string ExportName, string Access, string TypeFqn, LocationInfo Location);

    /// <summary>One <c>[RequestPreset]</c> field, with the problem that keeps it from being exported (or none).</summary>
    private sealed record PresetField(
        string HandlerDisplay,
        string FieldDisplay,
        string Name,
        LocationInfo Location,
        DiagnosticInfo? Problem);

    private static class TrackingNames {
        public const string Constants = "ClientConstantsClasses";
        public const string Presets = "RequestPresetFields";
        public const string Combined = "ClientExportsCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var constants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ClientConstantsAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => CreateConstantsClass(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) => models
                .OrderBy(static m => m.TypeFqn, StringComparer.Ordinal)
                .ToEquatableArray())
            .WithTrackingName(TrackingNames.Constants);

        var presets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RequestPresetAttributeMetadataName,
                static (node, _) => node is VariableDeclaratorSyntax,
                static (ctx, _) => CreatePresetField(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect()
            .Select(static (models, _) => models
                .OrderBy(static m => m.HandlerDisplay, StringComparer.Ordinal)
                .ThenBy(static m => m.FieldDisplay, StringComparer.Ordinal)
                .ToEquatableArray())
            .WithTrackingName(TrackingNames.Presets);

        var modules = ModuleProviders.CollectModules(context);
        var combined = constants.Combine(presets).Combine(modules).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var ((constantClasses, presetFields), moduleList) = source;
            ReportPresets(spc, presetFields);
            EmitConstants(spc, constantClasses, moduleList);
        });
    }

    private static PresetField? CreatePresetField(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not IFieldSymbol field) return null;

        var fmt = SymbolDisplayFormat.CSharpErrorMessageFormat;
        var handler = field.ContainingType;
        var fieldDisplay = $"{handler.ToDisplayString(fmt)}.{field.Name}";
        var location = LocationInfo.From(field.Locations.FirstOrDefault());
        var name = ctx.Attributes.Length > 0 && ctx.Attributes[0].ConstructorArguments.Length > 0
            ? ctx.Attributes[0].ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;

        DiagnosticInfo Invalid(string reason) {
            return DiagnosticInfo.Create(InvalidPreset, location, fieldDisplay, reason);
        }

        DiagnosticInfo? problem = null;
        if (string.IsNullOrWhiteSpace(name))
            problem = Invalid("its name is blank");
        else if (!field.IsStatic || !field.IsReadOnly)
            problem = Invalid("it is not static readonly");
        else if (!HandlerShape.TryResolve(handler, out var request, out _, out _))
            problem = Invalid($"'{handler.ToDisplayString(fmt)}' is not a handler");
        else if (!SymbolEqualityComparer.Default.Equals(field.Type, request))
            problem = Invalid($"its type '{field.Type.ToDisplayString(fmt)}' is not the handler's request type "
                              + $"'{request.ToDisplayString(fmt)}'");
        else if (!IsOnJsonRpc(handler))
            problem = DiagnosticInfo.Create(PresetWithoutMethod, location, fieldDisplay, handler.ToDisplayString(fmt));

        return new PresetField(handler.ToDisplayString(fmt), fieldDisplay, name, location, problem);
    }

    private static bool IsOnJsonRpc(INamedTypeSymbol handler) {
        foreach (var attribute in handler.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == RpcMethodEmission.HandlerAttributeMetadataName)
                return RpcMethodEmission.ReadTransports(attribute).OnJsonRpc;

        return false;
    }

    private static void ReportPresets(SourceProductionContext spc, EquatableArray<PresetField> fields) {
        foreach (var field in fields)
            if (field.Problem is { } problem)
                spc.ReportDiagnostic(problem.ToDiagnostic());

        foreach (var group in fields
                     .Where(static f => f.Problem is null)
                     .GroupBy(static f => (f.HandlerDisplay, f.Name)))
            foreach (var duplicate in group.Skip(1))
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    DuplicatePreset, duplicate.Location, group.Key.HandlerDisplay, group.Key.Name).ToDiagnostic());
    }

    private static ConstantsClass? CreateConstantsClass(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var fqn = SymbolDisplayFormat.FullyQualifiedFormat;
        var display = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
        var location = LocationInfo.From(type.Locations.FirstOrDefault());

        string? problem = null;
        if (!type.IsStatic)
            problem = "it is not a static class";
        else if (type.IsGenericType)
            problem = "it is generic";
        else
            for (var current = type; current is not null; current = current.ContainingType)
                if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                    or Accessibility.ProtectedOrInternal)) {
                    problem = $"'{current.Name}' is not accessible to its module (make it internal or public)";
                    break;
                }

        var members = ImmutableArray.CreateBuilder<ConstantMember>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        if (problem is null)
            foreach (var member in type.GetMembers()) {
                if (member.IsImplicitlyDeclared || !member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                    continue;

                ITypeSymbol memberType;
                string? mutable = null;
                switch (member) {
                    case IFieldSymbol field:
                        memberType = field.Type;
                        if (!field.IsConst && !field.IsReadOnly) mutable = "it is a mutable static field";
                        break;
                    case IPropertySymbol { IsIndexer: false } property:
                        memberType = property.Type;
                        if (property.GetMethod is null) mutable = "it has no getter";
                        else if (property.SetMethod is not null) mutable = "it has a setter";
                        break;
                    default:
                        continue;
                }

                var memberLocation = LocationInfo.From(member.Locations.FirstOrDefault());
                var reason = mutable ?? (IsExportable(memberType)
                    ? null
                    : $"its type '{memberType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}' is not exportable");
                if (reason is not null) {
                    diagnostics.Add(DiagnosticInfo.Create(UnexportableConstant, memberLocation, member.Name, display, reason));
                    continue;
                }

                members.Add(new ConstantMember(
                    LowerFirst(member.Name),
                    $"{type.ToDisplayString(fqn)}.{member.Name}",
                    memberType.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(fqn),
                    memberLocation));
            }

        return new ConstantsClass(
            ns, type.ToDisplayString(fqn), display, problem, location, members.ToImmutable(), diagnostics.ToImmutable());
    }

    // Numbers, strings, booleans and enums, and arrays, read-only lists or immutable arrays of them.
    private static bool IsExportable(ITypeSymbol type) {
        if (IsExportableScalar(type)) return true;

        if (type is IArrayTypeSymbol { Rank: 1 } array) return IsExportableScalar(array.ElementType);

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named) {
            var definition = named.OriginalDefinition.ToDisplayString();
            return definition is "System.Collections.Generic.IReadOnlyList<T>"
                       or "System.Collections.Generic.IReadOnlyCollection<T>"
                       or "System.Collections.Immutable.ImmutableArray<T>"
                   && IsExportableScalar(named.TypeArguments[0]);
        }

        return false;
    }

    private static bool IsExportableScalar(ITypeSymbol type) {
        return type.TypeKind == TypeKind.Enum || type.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_String or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
    }

    private static string LowerFirst(string name) {
        return name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
    }

    private static void EmitConstants(
        SourceProductionContext spc,
        EquatableArray<ConstantsClass> classes,
        EquatableArray<ModuleScanner.Module> modules) {
        var byModule = new Dictionary<ModuleScanner.Module, List<ConstantsClass>>();
        foreach (var model in classes) {
            foreach (var diagnostic in model.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            var problem = model.Problem;
            var module = problem is null ? ModuleScanner.FindBest(model.Namespace, modules) : null;
            if (problem is null && module is null)
                problem = $"its namespace '{model.Namespace}' is not under any [AppModule] of this assembly, so no "
                          + "module registers it";

            if (problem is not null) {
                spc.ReportDiagnostic(DiagnosticInfo.Create(InvalidConstantsClass, model.Location, model.Display, problem)
                    .ToDiagnostic());
                continue;
            }

            if (!byModule.TryGetValue(module!, out var list)) byModule[module!] = list = [];
            list.Add(model);
        }

        foreach (var pair in byModule.OrderBy(static p => p.Key.Name, StringComparer.Ordinal)) {
            var members = new List<ConstantMember>();
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var model in pair.Value)
            foreach (var member in model.Members) {
                if (owners.TryGetValue(member.ExportName, out var owner)) {
                    spc.ReportDiagnostic(DiagnosticInfo.Create(
                        DuplicateConstant, member.Location, pair.Key.Name, member.ExportName, owner, model.Display).ToDiagnostic());
                    continue;
                }

                owners[member.ExportName] = model.Display;
                members.Add(member);
            }

            if (members.Count == 0) continue;

            ModuleDefaultsEmitter.EmitFiller(
                spc,
                pair.Key.Namespace,
                pair.Key.TypeName,
                ModuleDefaultsEmitter.AddClientConstantsMethod,
                "ClientConstants",
                RegistrationBody(pair.Key.Name, members));
        }
    }

    // One ModuleClientConstants per module; each value is read lazily, by the schema export only.
    private static string RegistrationBody(string moduleName, List<ConstantMember> members) {
        var sb = new StringBuilder();
        sb.AppendLine(
            "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, "
            + $"new {ConstantsFqn} {{");
        sb.AppendLine($"            Module = {Literal(moduleName)},");
        sb.AppendLine($"            Constants = new {ConstantFqn}[] {{");
        foreach (var member in members)
            sb.AppendLine(
                $"                new() {{ Name = {Literal(member.ExportName)}, Type = typeof({member.TypeFqn}), "
                + $"GetValue = static () => {member.Access} }},");
        sb.Append("            }\n        });");
        return sb.ToString();
    }

    private static string Literal(string value) {
        return SymbolDisplay.FormatLiteral(value, true);
    }
}
