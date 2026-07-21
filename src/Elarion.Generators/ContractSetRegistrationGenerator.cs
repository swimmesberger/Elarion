using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Implements every partial method annotated with <c>[GenerateContractSetRegistration(typeof(TContract))]</c>,
/// composing every implementation of the contract declared in the same compilation. Contract sets are the
/// module-less counterpart to <c>[Service]</c>: the host authors, names, and places the composition method
/// and pulls it from its composition root; no bootstrapper invokes it and no configuration gates it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ContractSetRegistrationGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName =
        "Elarion.Abstractions.GenerateContractSetRegistrationAttribute";

    private const string ServiceCollectionFqn =
        "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private static readonly DiagnosticDescriptor InvalidContractSetContract = new(
        "ELSG014",
        "Contract-set type must be an interface or abstract class",
        "Contract-set type '{0}' must be a non-open-generic interface or abstract class",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ContractSetHasNoImplementations = new(
        "ELSG015",
        "Contract set has no implementations",
        "Contract set '{0}' has no implementations in this assembly; '{1}' registers nothing",
        "Elarion.Generators",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor DuplicateContractSetDeclaration = new(
        "ELSG016",
        "Duplicate contract-set declaration",
        "A contract set for '{0}' is declared more than once in this assembly",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor GenericContractSetImplementation = new(
        "ELSG017",
        "Generic contract-set implementations are not supported",
        "Generic implementation '{0}' of contract set '{1}' is not supported",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ContractSetImplementationIsAlsoService = new(
        "ELSG018",
        "Contract-set implementation also registers the contract through [Service]",
        "'{0}' registers under '{1}' through both [Service] and the contract set — once module-gated, once " +
        "unconditionally. Pick one mechanism for this contract.",
        "Elarion.Generators",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor InvalidContractSetMethod = new(
        "ELSG019",
        "Invalid contract-set method",
        "Contract-set method '{0}' must be declared 'static partial IServiceCollection {0}(this IServiceCollection services)' " +
        "on a non-generic static partial class, without a hand-written implementation",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// One annotated partial method the generator implements. <see cref="ContractFqn"/> is
    /// <see langword="null"/> when the contract argument was rejected (ELSG014) — the method still gets an
    /// empty implementation so the author sees one clear diagnostic instead of a cascading CS8795.
    /// </summary>
    private sealed record ContractDeclaration(
        string? ContractFqn,
        string Namespace,
        string ContainingTypeName,
        string MethodAccessibility,
        string MethodName,
        string ParameterName,
        string DescriptorFactory,
        string HintName,
        LocationInfo Location);

    /// <summary>A declaration attempt: either a declaration or the diagnostics that rejected it.</summary>
    private sealed record DeclarationResult(
        ContractDeclaration? Declaration,
        EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>A class in the compilation that is assignable to at least one declared contract.</summary>
    private sealed record CandidateMatch(
        string ImplementationFqn,
        bool IsGeneric,
        EquatableArray<string> MatchedContractFqns,
        EquatableArray<string> OverlappingServiceContractFqns,
        LocationInfo Location);

    /// <summary>Assignability facts for one class declaration, before matching against the declared contracts.</summary>
    private sealed record CandidateInfo(
        string ImplementationFqn,
        bool IsGeneric,
        EquatableArray<string> AssignableFqns,
        EquatableArray<string> ServiceContractFqns,
        LocationInfo Location);

    private static class TrackingNames {
        public const string Declarations = "ContractSetDeclarations";
        public const string Matches = "ContractSetMatches";
        public const string Combined = "ContractSetCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var declarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => CreateDeclarationResult(ctx))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Declarations);

        var matches = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null } cls &&
                                    !cls.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                                    !cls.Modifiers.Any(SyntaxKind.AbstractKeyword),
                static (ctx, _) => CreateCandidate(ctx))
            .Combine(declarations)
            .Select(static (pair, _) => Match(pair.Left, pair.Right))
            .Where(static match => match is not null)
            .Select(static (match, _) => match!)
            .Collect()
            .WithTrackingName(TrackingNames.Matches);

        var combined = declarations.Combine(matches).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => Emit(spc, source.Left, source.Right));
    }

    private static DeclarationResult? CreateDeclarationResult(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not IMethodSymbol method || ctx.Attributes.Length == 0) return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var location = LocationInfo.From((ctx.TargetNode as MethodDeclarationSyntax)?.Identifier.GetLocation());

        if (!IsValidContractSetMethod(method)) {
            return new DeclarationResult(
                null,
                ImmutableArray.Create(DiagnosticInfo.Create(InvalidContractSetMethod, location, method.Name)));
        }

        var containingType = method.ContainingType;
        var declaration = new ContractDeclaration(
            null,
            GetNamespace(containingType),
            containingType.Name,
            GetAccessibilityKeyword(method.DeclaredAccessibility),
            method.Name,
            method.Parameters[0].Name,
            GetDescriptorFactory(ctx.Attributes[0]),
            $"{HintNames.Sanitize(containingType.ToDisplayString(fmt))}_{method.Name}",
            location);

        // A missing or error-typed argument is already a compiler error; implement the method empty so the
        // author is not buried under a cascading CS8795 on top of it.
        var attribute = ctx.Attributes[0];
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol contract ||
            contract.TypeKind == TypeKind.Error)
            return new DeclarationResult(declaration, EquatableArray<DiagnosticInfo>.Empty);

        var isValidKind = contract.TypeKind == TypeKind.Interface ||
                          (contract.TypeKind == TypeKind.Class && contract.IsAbstract);
        if (!isValidKind || contract.IsUnboundGenericType) {
            return new DeclarationResult(
                declaration,
                ImmutableArray.Create(DiagnosticInfo.Create(
                    InvalidContractSetContract,
                    location,
                    contract.ToDisplayString(fmt))));
        }

        return new DeclarationResult(
            declaration with { ContractFqn = contract.ToDisplayString(fmt) },
            EquatableArray<DiagnosticInfo>.Empty);
    }

    private static bool IsValidContractSetMethod(IMethodSymbol method) {
        return method is {
                   IsStatic: true,
                   IsPartialDefinition: true,
                   PartialImplementationPart: null,
                   IsExtensionMethod: true,
                   Arity: 0,
                   Parameters.Length: 1
               } &&
               IsServiceCollection(method.ReturnType) &&
               IsServiceCollection(method.Parameters[0].Type) &&
               !IsGenericOrNestedInGenericType(method.ContainingType);
    }

    private static bool IsServiceCollection(ITypeSymbol type) {
        return string.Equals(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ServiceCollectionFqn,
            StringComparison.Ordinal);
    }

    private static string GetAccessibilityKeyword(Accessibility accessibility) {
        return accessibility switch {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Protected => "protected",
            _ => "internal"
        };
    }

    private static string GetDescriptorFactory(AttributeData attribute) {
        foreach (var namedArgument in attribute.NamedArguments)
            if (string.Equals(namedArgument.Key, "Scope", StringComparison.Ordinal) &&
                namedArgument.Value.Value is int scope)
                return scope switch {
                    0 => "Scoped",
                    2 => "Transient",
                    _ => "Singleton"
                };

        return "Singleton";
    }

    private static CandidateInfo? CreateCandidate(GeneratorSyntaxContext ctx) {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol classSymbol ||
            classSymbol.IsAbstract || classSymbol.IsStatic)
            return null;

        // Generated top-level code cannot reference a private/protected nested type; such a class can
        // never be part of a composed set, so it is not a candidate at all.
        if (!IsReferenceableFromGeneratedCode(classSymbol)) return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var assignable = ImmutableArray.CreateBuilder<string>();
        foreach (var iface in classSymbol.AllInterfaces)
            assignable.Add(iface.ToDisplayString(fmt));
        for (var baseType = classSymbol.BaseType;
             baseType is not null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
            assignable.Add(baseType.ToDisplayString(fmt));

        if (assignable.Count == 0) return null;

        var serviceContracts = ImmutableArray<string>.Empty;
        if (ServiceContractResolver.FindServiceAttribute(classSymbol) is { } serviceAttribute)
            serviceContracts = ServiceContractResolver.ResolveContractFqns(classSymbol, serviceAttribute, fmt);

        return new CandidateInfo(
            classSymbol.ToDisplayString(fmt),
            IsGenericOrNestedInGenericType(classSymbol),
            assignable.ToImmutable(),
            serviceContracts,
            LocationInfo.From(((ClassDeclarationSyntax)ctx.Node).Identifier.GetLocation()));
    }

    private static bool IsReferenceableFromGeneratedCode(INamedTypeSymbol classSymbol) {
        for (var current = classSymbol; current is not null; current = current.ContainingType)
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal))
                return false;

        return true;
    }

    private static bool IsGenericOrNestedInGenericType(INamedTypeSymbol classSymbol) {
        for (var current = classSymbol; current is not null; current = current.ContainingType)
            if (current.TypeParameters.Length > 0)
                return true;

        return false;
    }

    private static CandidateMatch? Match(CandidateInfo? candidate, ImmutableArray<DeclarationResult> declarations) {
        if (candidate is null || declarations.IsEmpty) return null;

        var matched = ImmutableArray.CreateBuilder<string>();
        var overlapping = ImmutableArray.CreateBuilder<string>();
        foreach (var result in declarations) {
            if (result.Declaration is not { ContractFqn: { } contractFqn } ||
                !Contains(candidate.AssignableFqns, contractFqn) ||
                Contains(matched, contractFqn))
                continue;

            matched.Add(contractFqn);
            if (Contains(candidate.ServiceContractFqns, contractFqn))
                overlapping.Add(contractFqn);
        }

        if (matched.Count == 0) return null;

        return new CandidateMatch(
            candidate.ImplementationFqn,
            candidate.IsGeneric,
            matched.ToImmutable(),
            overlapping.ToImmutable(),
            candidate.Location);
    }

    private static bool Contains(IReadOnlyList<string> values, string value) {
        foreach (var candidate in values)
            if (string.Equals(candidate, value, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static bool Contains(ImmutableArray<string>.Builder values, string value) {
        foreach (var candidate in values)
            if (string.Equals(candidate, value, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<DeclarationResult> results,
        ImmutableArray<CandidateMatch> matches) {
        foreach (var result in results)
        foreach (var diagnostic in result.Diagnostics)
            spc.ReportDiagnostic(diagnostic.ToDiagnostic());

        // Provider order is unspecified; sort everything before grouping so emission is deterministic.
        // Same-location duplicates guard against one attribute application surfacing through more than
        // one provider node; genuinely repeated declarations keep distinct locations and are ELSG016.
        var declarations = results
            .Where(static r => r.Declaration is not null)
            .Select(static r => r.Declaration!)
            .OrderBy(static d => d.HintName, StringComparer.Ordinal)
            .ThenBy(static d => d.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(static d => d.Location.TextSpan.Start)
            .GroupBy(static d => (d.HintName, d.Location))
            .Select(static g => g.First())
            .ToList();

        var uniqueMatches = matches
            .OrderBy(static m => m.ImplementationFqn, StringComparer.Ordinal)
            .ThenBy(static m => m.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(static m => m.Location.TextSpan.Start)
            .GroupBy(static m => m.ImplementationFqn)
            .Select(static g => g.First())
            .ToList();

        // First declaration per contract (deterministic by file position) wins; later ones are ELSG016 and
        // are implemented empty so the only error the author sees is ours, not a cascading CS8795.
        var seenContracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations
                     .Where(static d => d.ContractFqn is not null)
                     .OrderBy(static d => d.Location.FilePath, StringComparer.Ordinal)
                     .ThenBy(static d => d.Location.TextSpan.Start)) {
            var contractFqn = declaration.ContractFqn!;
            if (!seenContracts.Add(contractFqn)) {
                spc.ReportDiagnostic(DiagnosticInfo
                    .Create(DuplicateContractSetDeclaration, declaration.Location, contractFqn)
                    .ToDiagnostic());
                AddContractSetSource(spc, declaration, []);
                continue;
            }

            var implementations = new List<string>();
            foreach (var match in uniqueMatches) {
                if (!Contains(match.MatchedContractFqns, contractFqn)) continue;

                if (match.IsGeneric) {
                    spc.ReportDiagnostic(DiagnosticInfo
                        .Create(
                            GenericContractSetImplementation,
                            match.Location,
                            match.ImplementationFqn,
                            contractFqn)
                        .ToDiagnostic());
                    continue;
                }

                if (Contains(match.OverlappingServiceContractFqns, contractFqn))
                    spc.ReportDiagnostic(DiagnosticInfo
                        .Create(
                            ContractSetImplementationIsAlsoService,
                            match.Location,
                            match.ImplementationFqn,
                            contractFqn)
                        .ToDiagnostic());

                implementations.Add(match.ImplementationFqn);
            }

            if (implementations.Count == 0)
                spc.ReportDiagnostic(DiagnosticInfo
                    .Create(
                        ContractSetHasNoImplementations,
                        declaration.Location,
                        contractFqn,
                        declaration.MethodName)
                    .ToDiagnostic());

            AddContractSetSource(spc, declaration, implementations);
        }

        // ELSG014 declarations (invalid contract argument): the diagnostic is already reported; still
        // implement the method empty for the same anti-cascade reason as above.
        foreach (var declaration in declarations.Where(static d => d.ContractFqn is null))
            AddContractSetSource(spc, declaration, []);
    }

    private static void AddContractSetSource(
        SourceProductionContext spc,
        ContractDeclaration declaration,
        IReadOnlyList<string> implementations) {
        var code = GenerateContractSet(declaration, implementations);
        spc.AddSource($"{declaration.HintName}.g.cs", SourceText.From(code, Encoding.UTF8));
    }

    private static string GenerateContractSet(ContractDeclaration declaration, IReadOnlyList<string> implementations) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ContractSetRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        if (declaration.Namespace.Length > 0) {
            sb.AppendLine($"namespace {declaration.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {declaration.ContainingTypeName}");
        sb.AppendLine("{");
        sb.AppendLine(
            $"    {declaration.MethodAccessibility} static partial IServiceCollection {declaration.MethodName}(this IServiceCollection {declaration.ParameterName})");
        sb.AppendLine("    {");
        foreach (var implementation in implementations)
            sb.AppendLine(
                $"        {declaration.ParameterName}.TryAddEnumerable(ServiceDescriptor.{declaration.DescriptorFactory}<{declaration.ContractFqn}, {implementation}>());");

        sb.AppendLine($"        return {declaration.ParameterName};");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GetNamespace(INamedTypeSymbol typeSymbol) {
        var ns = typeSymbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace) return string.Empty;

        return ns.ToDisplayString();
    }
}
