using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Source generator that fills every partial class annotated with <c>[ResourceFilter&lt;TEntity&gt;]</c> with a
/// strongly-typed, reflection-free <c>IQueryAuthorizer&lt;TEntity&gt;</c> data-level authorization predicate.
/// The predicate composes the declared rules as <c>AND(scope rules) AND OR(grant rules)</c> — a row is visible
/// when it satisfies every scope (tenant) and at least one grant (owner, or a share recorded in the grants
/// table for the caller's user/roles) — as a plain typed expression the query provider translates to SQL.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ResourceFilterGenerator : IIncrementalGenerator {
    private const string ResourceFilterAttributeName = ElarionGeneratorConventions.ResourceFilterAttribute;
    private const string CurrentUserType = "global::Elarion.Abstractions.Identity.ICurrentUser";
    private const string OperationType = "global::Elarion.Abstractions.Authorization.ResourceOperation";
    private const string QueryAuthorizerType = ElarionGeneratorConventions.QueryAuthorizerTypeFqn;
    private const string GrantSourceType = "global::Elarion.Authorization.EntityFrameworkCore.IResourceGrantSource";
    private const string ServiceCollectionType = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private const string ServiceCollectionExtensions =
        "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";

    private static readonly DiagnosticDescriptor UnknownProperty = new(
        "ELRES001",
        "ResourceFilter property does not match a property",
        "Entity '{0}' has a [ResourceFilter] rule property '{1}' that does not match any property; no filter will be generated",
        "Elarion.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedPropertyType = new(
        "ELRES002",
        "ResourceFilter property type is not supported",
        "Entity '{0}' has a [ResourceFilter] rule property '{1}' of type '{2}', which is not supported; use a non-nullable Guid, string, int, or long; no filter will be generated",
        "Elarion.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidFilterClass = new(
        "ELRES003",
        "ResourceFilter class must be a top-level partial class",
        "ResourceFilter class '{0}' must be a non-nested partial class; the generator emits the filter definition into it",
        "Elarion.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NoRules = new(
        "ELRES004",
        "ResourceFilter declares no rules",
        "ResourceFilter class '{0}' declares no rules; set OwnerProperty, TenantProperty, and/or Shared so a predicate can be generated",
        "Elarion.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor SharedWithoutResourceType = new(
        "ELRES005",
        "ResourceFilter Shared rule requires ResourceTypeName",
        "ResourceFilter class '{0}' sets Shared = true but no ResourceTypeName; set ResourceTypeName so the grant lookup can match the resource type",
        "Elarion.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ResourceFilterAttributeName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .WithTrackingName("ResourceFilterTargets");

        context.RegisterSourceOutput(
            targets,
            static (spc, target) => {
                if (target is null) return;

                foreach (var diagnostic in target.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

                if (target.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return;

                Emit(spc, target);
            });
    }

    private static ResourceFilterTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol filterClass) return null;

        // The filter class carries the definition; the entity comes from the generic type argument.
        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].AttributeClass is not { TypeArguments.Length: 1 } attributeClass ||
            attributeClass.TypeArguments[0] is not INamedTypeSymbol entity)
            return null;

        var location = LocationModel.From(filterClass);
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticModel>();

        // The generator completes the class as a partial; a nested or non-partial class cannot be filled.
        var isPartial = ctx.TargetNode is TypeDeclarationSyntax typeDecl &&
                        typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
        if (!isPartial || filterClass.ContainingType is not null)
            diagnostics.Add(DiagnosticModel.Create(InvalidFilterClass, location, filterClass.Name));

        string? ownerProperty = null;
        string? tenantProperty = null;
        var tenantClaimType = "tenant";
        var shared = false;
        string? resourceTypeName = null;
        var idProperty = "Id";
        foreach (var argument in ctx.Attributes[0].NamedArguments)
            switch (argument.Key) {
                case "OwnerProperty":
                    ownerProperty = argument.Value.Value as string;
                    break;
                case "TenantProperty":
                    tenantProperty = argument.Value.Value as string;
                    break;
                case "TenantClaimType":
                    if (argument.Value.Value is string claim && claim.Length > 0) tenantClaimType = claim;

                    break;
                case "Shared":
                    shared = argument.Value.Value is true;
                    break;
                case "ResourceTypeName":
                    resourceTypeName = argument.Value.Value as string;
                    break;
                case "IdProperty":
                    if (argument.Value.Value is string id && id.Length > 0) idProperty = id;

                    break;
            }

        var rules = ImmutableArray.CreateBuilder<RuleModel>();
        var properties = GetAccessibleProperties(entity);

        // Owner is a grant rule (OR-combined); tenant is a scope rule (AND-combined). Order is fixed so the
        // emitted source is byte-identical across runs.
        if (!string.IsNullOrEmpty(ownerProperty))
            AddPropertyRule(rules, diagnostics, properties, entity, location, RuleKindGrant, ownerProperty!, null);

        if (!string.IsNullOrEmpty(tenantProperty))
            AddPropertyRule(rules, diagnostics, properties, entity, location, RuleKindScope, tenantProperty!,
                tenantClaimType);

        string? idParseKind = null;
        if (shared) {
            if (string.IsNullOrEmpty(resourceTypeName))
                diagnostics.Add(DiagnosticModel.Create(SharedWithoutResourceType, location, filterClass.Name));

            if (!properties.TryGetValue(idProperty, out var idProp)) {
                diagnostics.Add(DiagnosticModel.Create(UnknownProperty, location, entity.Name, idProperty));
            }
            else {
                idParseKind = ResolveParseKind(idProp.Type);
                if (idParseKind is null)
                    diagnostics.Add(DiagnosticModel.Create(
                        UnsupportedPropertyType, location, entity.Name, idProperty, idProp.Type.ToDisplayString()));
            }
        }

        if (string.IsNullOrEmpty(ownerProperty) && string.IsNullOrEmpty(tenantProperty) && !shared)
            diagnostics.Add(DiagnosticModel.Create(NoRules, location, filterClass.Name));

        return new ResourceFilterTarget(
            filterClass.Name,
            filterClass.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : filterClass.ContainingNamespace.ToDisplayString(),
            filterClass.ToDisplayString(FullyQualified),
            entity.ToDisplayString(FullyQualified),
            rules.ToImmutable(),
            shared,
            resourceTypeName ?? string.Empty,
            idProperty,
            idParseKind ?? string.Empty,
            diagnostics.ToImmutable());
    }

    private static void AddPropertyRule(
        ImmutableArray<RuleModel>.Builder rules,
        ImmutableArray<DiagnosticModel>.Builder diagnostics,
        Dictionary<string, IPropertySymbol> properties,
        INamedTypeSymbol entity,
        LocationModel location,
        string kind,
        string propertyName,
        string? claimType) {
        if (!properties.TryGetValue(propertyName, out var property)) {
            diagnostics.Add(DiagnosticModel.Create(UnknownProperty, location, entity.Name, propertyName));
            return;
        }

        var parseKind = ResolveParseKind(property.Type);
        if (parseKind is null) {
            diagnostics.Add(DiagnosticModel.Create(
                UnsupportedPropertyType, location, entity.Name, propertyName, property.Type.ToDisplayString()));
            return;
        }

        rules.Add(new RuleModel(kind, propertyName, parseKind, claimType));
    }

    private static string? ResolveParseKind(ITypeSymbol type) {
        if (IsNullable(type)) return null;

        switch (type.SpecialType) {
            case SpecialType.System_String:
                return "string";
            case SpecialType.System_Int32:
                return "int";
            case SpecialType.System_Int64:
                return "long";
        }

        return type.ToDisplayString() == "System.Guid" ? "guid" : null;
    }

    private static Dictionary<string, IPropertySymbol> GetAccessibleProperties(INamedTypeSymbol entity) {
        var result = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        for (var type = entity; type is not null; type = type.BaseType)
            foreach (var member in type.GetMembers())
                if (member is IPropertySymbol { IsStatic: false, GetMethod: not null } property &&
                    !result.ContainsKey(property.Name))
                    result.Add(property.Name, property);

        return result;
    }

    private static bool IsNullable(ITypeSymbol type) {
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return true;

        return type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated;
    }

    private static void Emit(SourceProductionContext context, ResourceFilterTarget target) {
        if (target.Rules.IsEmpty && !target.Shared) return;

        var entity = target.EntityGlobalFqn;
        var className = target.ClassName;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        var hasNamespace = target.ClassNamespace.Length > 0;
        if (hasNamespace) {
            sb.AppendLine($"namespace {target.ClassNamespace};");
            sb.AppendLine();
        }

        // Complete the user's partial filter class as the authorizer; the entity is referenced by its
        // fully-qualified name, so it can live in any namespace and stays free of authorization attributes.
        sb.AppendLine($"partial class {className} : {QueryAuthorizerType}<{entity}>");
        sb.AppendLine("{");

        if (target.Shared) {
            // A shared filter consults the grants set (an EXISTS), so it is a scoped service rather than a
            // stateless singleton; the host registers it with Register (or the generated DI aggregation).
            sb.AppendLine($"    private readonly {GrantSourceType} __grants;");
            sb.AppendLine();
            sb.AppendLine($"    public {className}({GrantSourceType} grants) => __grants = grants;");
            sb.AppendLine();
            sb.AppendLine($"    public static void Register({ServiceCollectionType} services)");
            sb.AppendLine(
                $"        => {ServiceCollectionExtensions}.AddScoped<{QueryAuthorizerType}<{entity}>, {className}>(services);");
            sb.AppendLine();
            EmitSharedGetFilter(sb, target, entity);
        }
        else {
            sb.AppendLine($"    public static {className} Specification {{ get; }} = new();");
            sb.AppendLine();
            sb.AppendLine($"    private {className}() {{ }}");
            sb.AppendLine();
            EmitGetFilter(sb, target, entity);
        }

        sb.AppendLine("}");

        // Hint name keys on the filter class (not the entity), mirroring the keyset generator.
        var hint = target.ClassGlobalFqn
            .Replace("global::", string.Empty)
            .Replace('.', '_')
            .Replace('+', '_');
        context.AddSource($"{hint}.ResourceFilter.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitGetFilter(StringBuilder sb, ResourceFilterTarget target, string entity) {
        sb.AppendLine(
            $"    public global::System.Linq.Expressions.Expression<global::System.Func<{entity}, bool>>? GetFilter(");
        sb.AppendLine($"        {CurrentUserType} user,");
        sb.AppendLine($"        {OperationType} operation)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!user.IsAuthenticated)");
        sb.AppendLine("        {");
        sb.AppendLine("            return __e => false;");
        sb.AppendLine("        }");
        sb.AppendLine();

        var scopeTerms = new List<string>();
        var grantTerms = new List<string>();
        for (var i = 0; i < target.Rules.Length; i++) {
            var rule = target.Rules[i];
            var keyVar = $"__key{i}";
            EmitKeyResolution(sb, rule, keyVar);
            sb.AppendLine();

            var term = $"__e.{rule.PropertyName} == {keyVar}";
            if (rule.Kind == RuleKindScope)
                scopeTerms.Add(term);
            else
                grantTerms.Add(term);
        }

        sb.AppendLine($"        return __e => {ComposeBody(scopeTerms, grantTerms)};");
        sb.AppendLine("    }");
    }

    private static void EmitSharedGetFilter(StringBuilder sb, ResourceFilterTarget target, string entity) {
        sb.AppendLine(
            $"    public global::System.Linq.Expressions.Expression<global::System.Func<{entity}, bool>>? GetFilter(");
        sb.AppendLine($"        {CurrentUserType} user,");
        sb.AppendLine($"        {OperationType} operation)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!user.IsAuthenticated)");
        sb.AppendLine("        {");
        sb.AppendLine("            return __e => false;");
        sb.AppendLine("        }");
        sb.AppendLine();

        var scopeTerms = new List<string>();
        var grantTerms = new List<string>();
        for (var i = 0; i < target.Rules.Length; i++) {
            var rule = target.Rules[i];
            var keyVar = $"__key{i}";
            if (rule.Kind == RuleKindScope) {
                // A scope that cannot be resolved means no access at all (AND), so fail closed.
                EmitKeyResolution(sb, rule, keyVar);
                sb.AppendLine();
                scopeTerms.Add($"__e.{rule.PropertyName} == {keyVar}");
            }
            else if (rule.ParseKind == "string") {
                sb.AppendLine($"        var {keyVar} = user.UserId;");
                sb.AppendLine();
                grantTerms.Add($"__e.{rule.PropertyName} == {keyVar}");
            }
            else {
                // An owner key that does not parse just disqualifies the owner grant; a share may still apply.
                var okVar = $"__ok{i}";
                sb.AppendLine(
                    $"        var {okVar} = {TryParseMethod(rule.ParseKind)}(user.UserId, out var {keyVar});");
                sb.AppendLine();
                grantTerms.Add($"({okVar} && __e.{rule.PropertyName} == {keyVar})");
            }
        }

        sb.AppendLine("        var __op = operation.Name;");
        sb.AppendLine("        var __uid = user.UserId;");
        sb.AppendLine("        var __roles = user.Roles;");
        sb.AppendLine("        var __grantsQuery = __grants.Grants;");
        sb.AppendLine();

        var idExpression = target.IdParseKind == "string"
            ? $"__e.{target.IdProperty}"
            : $"__e.{target.IdProperty}.ToString()";
        grantTerms.Add(
            $"global::System.Linq.Queryable.Any(__grantsQuery, __g => __g.ResourceType == \"{Escape(target.ResourceTypeName)}\""
            + $" && __g.ResourceId == {idExpression}"
            + " && __g.Operation == __op"
            + " && ((__g.PrincipalKind == \"user\" && __g.PrincipalId == __uid)"
            + " || (__g.PrincipalKind == \"role\" && global::System.Linq.Enumerable.Contains(__roles, __g.PrincipalId))))");

        sb.AppendLine($"        return __e => {ComposeBody(scopeTerms, grantTerms)};");
        sb.AppendLine("    }");
    }

    private static string ComposeBody(List<string> scopeTerms, List<string> grantTerms) {
        if (scopeTerms.Count > 0 && grantTerms.Count > 0)
            return string.Join(" && ", scopeTerms) + " && (" + string.Join(" || ", grantTerms) + ")";

        return grantTerms.Count > 0
            ? string.Join(" || ", grantTerms)
            : string.Join(" && ", scopeTerms);
    }

    private static void EmitKeyResolution(StringBuilder sb, RuleModel rule, string keyVar) {
        var source = rule.ClaimType is null
            ? "user.UserId"
            : $"global::System.Linq.Enumerable.FirstOrDefault(user.GetClaimValues(\"{Escape(rule.ClaimType)}\"))";

        if (rule.ParseKind == "string") {
            sb.AppendLine($"        var {keyVar} = {source};");
            if (rule.ClaimType is not null) {
                sb.AppendLine($"        if ({keyVar} is null)");
                sb.AppendLine("        {");
                sb.AppendLine("            return __e => false;");
                sb.AppendLine("        }");
            }

            return;
        }

        sb.AppendLine($"        if (!{TryParseMethod(rule.ParseKind)}({source}, out var {keyVar}))");
        sb.AppendLine("        {");
        sb.AppendLine("            return __e => false;");
        sb.AppendLine("        }");
    }

    private static string TryParseMethod(string parseKind) {
        return parseKind switch {
            "guid" => "global::System.Guid.TryParse",
            "int" => "global::System.Int32.TryParse",
            "long" => "global::System.Int64.TryParse",
            _ => "global::System.Guid.TryParse"
        };
    }

    private static string Escape(string value) {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private const string RuleKindScope = "scope";
    private const string RuleKindGrant = "grant";

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private sealed record RuleModel(string Kind, string PropertyName, string ParseKind, string? ClaimType);

    private sealed record ResourceFilterTarget(
        string ClassName,
        string ClassNamespace,
        string ClassGlobalFqn,
        string EntityGlobalFqn,
        EquatableArray<RuleModel> Rules,
        bool Shared,
        string ResourceTypeName,
        string IdProperty,
        string IdParseKind,
        EquatableArray<DiagnosticModel> Diagnostics);

    private sealed record DiagnosticModel(
        DiagnosticDescriptor Descriptor,
        LocationModel Location,
        EquatableArray<string> Args) {
        public DiagnosticSeverity Severity => Descriptor.DefaultSeverity;

        public static DiagnosticModel Create(DiagnosticDescriptor descriptor, LocationModel location,
            params string[] args) {
            return new DiagnosticModel(descriptor, location, args.ToImmutableArray());
        }

        public Diagnostic ToDiagnostic() {
            return Diagnostic.Create(Descriptor, Location.ToLocation(), [.. Args]);
        }
    }

    private readonly record struct LocationModel(string? FilePath, TextSpan Span, LinePositionSpan LineSpan) {
        public static LocationModel From(ISymbol symbol) {
            var location = symbol.Locations.FirstOrDefault();
            if (location is null || location.SourceTree is null) return new LocationModel(null, default, default);

            return new LocationModel(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
        }

        public Location? ToLocation() {
            return FilePath is null ? null : Location.Create(FilePath, Span, LineSpan);
        }
    }
}
