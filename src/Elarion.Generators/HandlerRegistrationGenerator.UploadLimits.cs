using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Declared upload limits (<c>[UploadLimits]</c>, ADR-0100): finds, at compile time, the members of a handler's request
/// type that declare limits for an <c>UploadedFile</c> (or a collection of them), and emits the reflection-free function
/// the <c>UploadLimitsDecorator</c> runs before the handler to declare them to the scoped
/// <c>IDeclaredUploadConstraints</c>. A declaration the pipeline would not enforce — on another type, or on a member
/// that holds no uploaded file — is a build error, never a silently unchecked file.
/// </summary>
public sealed partial class HandlerRegistrationGenerator {
    internal const string UploadLimitsAttributeMetadataName = "Elarion.Abstractions.UploadLimitsAttribute";
    private const string UploadedFileMetadataName = "Elarion.Abstractions.UploadedFile";
    private const string UploadConstraintsFqn = "global::Elarion.Abstractions.UploadConstraints";
    private const string DeclaredUploadConstraintsFqn = "global::Elarion.Abstractions.IDeclaredUploadConstraints";
    private const string SortMapMetadataName = "Elarion.Paging.SortMap<T>";

    private static readonly DiagnosticDescriptor InvalidUploadLimitsDescriptor = new(
        "ELUPL001",
        "Invalid [UploadLimits] declaration",
        "[UploadLimits] on '{0}' is not enforced because {1}; declare it on an UploadedFile member (or a collection of "
        + "UploadedFile) with MaxBytes > 0, a non-blank content type or a file extension such as \".xlsx\"",
        "Elarion.Abstractions",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UploadLimitsOutsideRequestDescriptor = new(
        "ELUPL002",
        "[UploadLimits] is not on a handler request",
        "[UploadLimits] on '{0}' is not enforced because '{1}' is not the request type of a handler in this assembly; "
        + "the pipeline applies declared limits to the members of a handler's request only, so declare them there",
        "Elarion.Abstractions",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor AmbiguousSortMapDescriptor = new(
        "ELRPC012",
        "Handler declares more than one sort map",
        "Handler '{0}' declares {1} static SortMap fields, so the schema cannot tell which one its sort parameter uses "
        + "and exports no sort keys for it; keep one sort map per handler",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>The upload members of a handler's request that declare limits, and every declaration site it consumed.</summary>
    internal sealed record UploadLimitsInfo(
        EquatableArray<UploadLimitMember> Members,
        // "{declaring type FQN}|{member name}" for every [UploadLimits] the request carries, valid or not, so the
        // misplaced-declaration check (ELUPL002) knows which ones a handler accounts for.
        EquatableArray<string> Sites);

    /// <summary>One upload member: its property name, whether it is a collection, and its declared limits.</summary>
    internal sealed record UploadLimitMember(
        string Property,
        bool IsCollection,
        long MaxBytes,
        EquatableArray<string> ContentTypes,
        EquatableArray<string> FileExtensions);

    /// <summary>One <c>[UploadLimits]</c> written in this compilation, for the misplaced-declaration check.</summary>
    internal sealed record UploadLimitsSite(string Key, string Display, string DeclaringType, LocationInfo Location);

    // Every [UploadLimits] property or constructor parameter of this compilation. A parameter's declaring type is its
    // constructor's type (a positional record); a parameter of anything else can never be a request member.
    private static UploadLimitsSite? CreateUploadLimitsSite(GeneratorAttributeSyntaxContext ctx) {
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var errorFmt = SymbolDisplayFormat.CSharpErrorMessageFormat;
        var location = LocationInfo.From(
            ctx.Attributes.FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax().GetLocation()
            ?? ctx.TargetSymbol.Locations.FirstOrDefault());
        return ctx.TargetSymbol switch {
            IPropertySymbol property => new UploadLimitsSite(
                $"{property.ContainingType.ToDisplayString(fmt)}|{property.Name}",
                $"{property.ContainingType.ToDisplayString(errorFmt)}.{property.Name}",
                property.ContainingType.ToDisplayString(errorFmt),
                location),
            IParameterSymbol { ContainingSymbol: IMethodSymbol method } parameter => new UploadLimitsSite(
                method.MethodKind == MethodKind.Constructor
                    ? $"{method.ContainingType.ToDisplayString(fmt)}|{parameter.Name}"
                    : string.Empty,
                $"{method.ContainingType.ToDisplayString(errorFmt)}.{parameter.Name}",
                method.ContainingType.ToDisplayString(errorFmt),
                location),
            _ => null
        };
    }

    private static void ReportMisplacedUploadLimits(
        SourceProductionContext spc,
        EquatableArray<HandlerInfo> handlers,
        EquatableArray<UploadLimitsSite> sites) {
        if (sites.IsEmpty) return;

        var accounted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handler in handlers)
            if (handler.UploadLimits is { } limits)
                foreach (var site in limits.Sites)
                    accounted.Add(site);

        foreach (var site in sites)
            if (!accounted.Contains(site.Key))
                spc.ReportDiagnostic(DiagnosticInfo.Create(
                    UploadLimitsOutsideRequestDescriptor, site.Location, site.Display, site.DeclaringType).ToDiagnostic());
    }

    // The request's [UploadLimits] members: public instance properties, its own and inherited, with the attribute on the
    // property or on the positional record's constructor parameter of the same name. The error contract reads it too
    // (ErrorContractDiscovery), so the codes a declaration implies follow exactly the members the pipeline enforces.
    internal static UploadLimitsInfo? ParseUploadLimits(
        ITypeSymbol requestType,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var attributeType = compilation.GetTypeByMetadataName(UploadLimitsAttributeMetadataName);
        var uploadedFile = compilation.GetTypeByMetadataName(UploadedFileMetadataName);
        if (attributeType is null || uploadedFile is null || requestType is not INamedTypeSymbol request)
            return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var errorFmt = SymbolDisplayFormat.CSharpErrorMessageFormat;
        var members = ImmutableArray.CreateBuilder<UploadLimitMember>();
        var sites = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = request; type is not null; type = type.BaseType)
            foreach (var member in type.GetMembers()) {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false } property || !seen.Add(property.Name))
                    continue;

                var (attribute, siteType) = FindUploadLimits(property, attributeType);
                if (attribute is null) continue;

                sites.Add($"{siteType!.ToDisplayString(fmt)}|{property.Name}");
                var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                var display = $"{type.ToDisplayString(errorFmt)}.{property.Name}";
                var (maxBytes, contentTypes, fileExtensions, problem) = ReadUploadLimits(attribute);
                var isCollection = false;
                if (problem is null)
                    if (IsUploadedFile(property.Type, uploadedFile)) {
                        // A single file.
                    }
                    else if (UploadElementType(property.Type) is { } element && IsUploadedFile(element, uploadedFile)) {
                        isCollection = true;
                    }
                    else {
                        problem = $"its type '{property.Type.ToDisplayString(errorFmt)}' holds no UploadedFile";
                    }

                if (problem is null && (property.GetMethod is null || !IsAccessibleFromAssembly(property.GetMethod, compilation)))
                    problem = "its getter is not accessible to the generated pipeline";

                if (problem is not null) {
                    diagnostics.Add(DiagnosticInfo.Create(InvalidUploadLimitsDescriptor, location, display, problem));
                    continue;
                }

                members.Add(new UploadLimitMember(property.Name, isCollection, maxBytes, contentTypes, fileExtensions));
            }

        if (sites.Count == 0) return null;

        // Declaration order is the member order the walk found; sorted for a stable emission across partial files.
        return new UploadLimitsInfo(
            members.OrderBy(static m => m.Property, StringComparer.Ordinal).ToEquatableArray(),
            sites.OrderBy(static s => s, StringComparer.Ordinal).ToEquatableArray());
    }

    private static (AttributeData? Attribute, INamedTypeSymbol? SiteType) FindUploadLimits(
        IPropertySymbol property, INamedTypeSymbol attributeType) {
        foreach (var attribute in property.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                return (attribute, property.ContainingType);

        foreach (var constructor in property.ContainingType.InstanceConstructors)
        foreach (var parameter in constructor.Parameters) {
            if (parameter.Name != property.Name) continue;

            foreach (var attribute in parameter.GetAttributes())
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                    return (attribute, property.ContainingType);
        }

        return (null, null);
    }

    private static (long MaxBytes, EquatableArray<string> ContentTypes, EquatableArray<string> FileExtensions, string? Problem)
        ReadUploadLimits(AttributeData attribute) {
        long maxBytes = 0;
        var contentTypes = ImmutableArray.CreateBuilder<string>();
        var fileExtensions = ImmutableArray.CreateBuilder<string>();
        foreach (var named in attribute.NamedArguments)
            switch (named.Key) {
                case "MaxBytes" when named.Value.Value is long value:
                    maxBytes = value;
                    break;
                case "ContentTypes" when named.Value.Kind == TypedConstantKind.Array && !named.Value.IsNull:
                    foreach (var item in named.Value.Values) {
                        if (item.Value is not string contentType || string.IsNullOrWhiteSpace(contentType))
                            return Invalid("it lists a blank content type");

                        contentTypes.Add(contentType);
                    }

                    break;
                case "FileExtensions" when named.Value.Kind == TypedConstantKind.Array && !named.Value.IsNull:
                    foreach (var item in named.Value.Values) {
                        if (item.Value is not string extension || !IsFileExtension(extension))
                            return Invalid(
                                $"it lists the file extension '{item.Value as string}', which is not a dot followed by "
                                + "a name without white space or path separators (for example \".xlsx\")");

                        fileExtensions.Add(extension);
                    }

                    break;
            }

        if (maxBytes < 0) return Invalid("MaxBytes is negative");

        if (maxBytes == 0 && contentTypes.Count == 0 && fileExtensions.Count == 0) return Invalid("it declares no limit");

        return (maxBytes, contentTypes.ToImmutable(), fileExtensions.ToImmutable(), null);

        static (long, EquatableArray<string>, EquatableArray<string>, string?) Invalid(string problem) {
            return (0, EquatableArray<string>.Empty, EquatableArray<string>.Empty, problem);
        }
    }

    // ".xlsx", ".tar.gz": a leading dot and at least one more character, none of them white space, a path separator or
    // a control character — the form an HTML accept attribute takes, and a suffix the stored file name can end in.
    private static bool IsFileExtension(string extension) {
        if (extension.Length < 2 || extension[0] != '.' || extension[extension.Length - 1] == '.') return false;

        foreach (var character in extension)
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\')
                return false;

        return true;
    }

    private static bool IsUploadedFile(ITypeSymbol type, INamedTypeSymbol uploadedFile) {
        return SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, uploadedFile);
    }

    // The element of an array or of an IEnumerable<T> (the type itself or one it implements), or null.
    private static ITypeSymbol? UploadElementType(ITypeSymbol type) {
        if (type is IArrayTypeSymbol { Rank: 1 } array) return array.ElementType;

        if (type is INamedTypeSymbol { IsGenericType: true } named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return named.TypeArguments[0];

        foreach (var candidate in type.AllInterfaces)
            if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return candidate.TypeArguments[0];

        return null;
    }

    private static bool IsAccessibleFromAssembly(IMethodSymbol getter, Compilation compilation) {
        return getter.DeclaredAccessibility == Accessibility.Public
               || (getter.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal
                   && SymbolEqualityComparer.Default.Equals(getter.ContainingAssembly, compilation.Assembly));
    }

    // ELRPC012: the schema exports the keys of the one SortMap a handler declares; with several it cannot tell which.
    private static void VerifySortMaps(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var count = 0;
        foreach (var member in classSymbol.GetMembers())
            if (member is IFieldSymbol { IsStatic: true } field
                && field.Type.OriginalDefinition.ToDisplayString() == SortMapMetadataName)
                count++;

        if (count < 2) return;

        foreach (var attribute in classSymbol.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == RpcMethodEmission.HandlerAttributeMetadataName
                && RpcMethodEmission.ReadTransports(attribute).OnJsonRpc) {
                diagnostics.Add(DiagnosticInfo.Create(
                    AmbiguousSortMapDescriptor,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    count.ToString(CultureInfo.InvariantCulture)));
                return;
            }
    }

    // The constraints of each upload member as static fields, and the function the decorator calls before the handler.
    private static void AppendUploadLimitsMembers(StringBuilder sb, HandlerInfo handler) {
        if (handler.UploadLimits is not { Members.IsEmpty: false } limits)
            return;

        sb.AppendLine();
        for (var i = 0; i < limits.Members.Count; i++) {
            var member = limits.Members[i];
            var parts = new List<string>(3);
            if (member.MaxBytes > 0)
                parts.Add($"MaxBytes = {member.MaxBytes.ToString(CultureInfo.InvariantCulture)}L");
            if (!member.ContentTypes.IsEmpty)
                parts.Add("ContentTypes = " + StringArrayLiteral(member.ContentTypes));
            if (!member.FileExtensions.IsEmpty)
                parts.Add("FileExtensions = " + StringArrayLiteral(member.FileExtensions));

            sb.AppendLine(
                $"    private static readonly {UploadConstraintsFqn} __uploadLimits{i} = new() {{ {string.Join(", ", parts)} }};");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"    private static void __DeclareUploadLimits({handler.RequestFqn} request, {DeclaredUploadConstraintsFqn} uploads)");
        sb.AppendLine("    {");
        for (var i = 0; i < limits.Members.Count; i++) {
            var member = limits.Members[i];
            if (member.IsCollection) {
                sb.AppendLine($"        if (request.{member.Property} is {{ }} __files{i})");
                sb.AppendLine($"            foreach (var __file in __files{i})");
                sb.AppendLine($"                uploads.Declare(__file, __uploadLimits{i});");
            }
            else {
                sb.AppendLine($"        uploads.Declare(request.{member.Property}, __uploadLimits{i});");
            }
        }

        sb.AppendLine("    }");
    }

    private static string StringArrayLiteral(EquatableArray<string> values) {
        return "new string[] { " + string.Join(", ", values.Select(static v => SymbolDisplay.FormatLiteral(v, true))) + " }";
    }

    // Innermost: the limits are declared right before the handler runs, after every gate, so a refused, replayed or
    // cached call declares nothing. The declarations are resolved softly so the decorator can refuse with a clear error.
    private static void AppendUploadLimitsDecorator(StringBuilder sb, HandlerInfo handler) {
        if (handler.UploadLimits is not { Members.IsEmpty: false })
            return;

        sb.AppendLine(
            $"                handler = new global::Elarion.Pipeline.UploadLimitsDecorator<{handler.RequestFqn}, {handler.ResponseFqn}>(");
        sb.AppendLine("                    handler,");
        sb.AppendLine($"                    sp.GetService<{DeclaredUploadConstraintsFqn}>(),");
        sb.AppendLine("                    __DeclareUploadLimits);");
        AppendPipelineStep(sb, "                ", "global::Elarion.Pipeline.UploadLimitsDecorator<,>", false);
    }
}
