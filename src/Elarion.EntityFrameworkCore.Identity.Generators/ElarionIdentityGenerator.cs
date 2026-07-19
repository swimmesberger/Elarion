using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.EntityFrameworkCore.Identity.Generators;

/// <summary>
/// Fills every partial <c>DbContext</c> annotated with <c>[GenerateElarionIdentity&lt;TUser, TRole, TKey&gt;]</c>
/// with the seven Identity <c>DbSet</c> properties and an implementation of the EF generator's per-feature
/// model-configuration seam that calls <c>ApplyElarionIdentity</c> with the attribute's types baked in.
/// So the host writes neither the DbSets nor the Identity model configuration, and never inherits
/// <c>IdentityDbContext</c> — and the neutral <c>OnEntitiesConfigured</c> seam stays reserved for the host.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionIdentityGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName =
        "Elarion.EntityFrameworkCore.Identity.GenerateElarionIdentityAttribute`3";

    private const string GenerateDbSetsAttributeName = "Elarion.EntityFrameworkCore.GenerateDbSetsAttribute";
    private const string IdentityNamespace = "global::Microsoft.AspNetCore.Identity";

    // The per-feature seam DbContextGenerator declares for this attribute — both sides derive the name from the
    // same convention, so they cannot drift, and OnEntitiesConfigured stays the host's own extension point.
    private static readonly string SeamMethodName =
        ElarionGeneratorConventions.ModelConfigurationSeamName("GenerateElarionIdentityAttribute");

    private static readonly DiagnosticDescriptor MissingGenerateDbSets = new(
        "ELIDN001",
        "[GenerateElarionIdentity] requires [GenerateDbSets]",
        "Context '{0}' is annotated with [GenerateElarionIdentity] but not [GenerateDbSets]; add [GenerateDbSets] "
        + "so the Identity DbSets and model-configuration seam are generated",
        "Elarion.EntityFrameworkCore.Identity",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .Where(static target => target is not null)
            .WithTrackingName("ElarionIdentityTargets");

        context.RegisterSourceOutput(targets, static (spc, target) => {
            if (target is null) return;

            foreach (var diagnostic in target.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (target.Emit) Emit(spc, target);
        });
    }

    private sealed record IdentityTarget(
        string Namespace,
        string ContextName,
        string HintName,
        string UserFqn,
        string RoleFqn,
        string KeyFqn,
        bool SnakeCase,
        string? Schema,
        string? TablePrefix,
        bool Emit,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static IdentityTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol contextSymbol) return null;

        if (ctx.Attributes.Length == 0 ||
            ctx.Attributes[0].AttributeClass is not { TypeArguments.Length: 3 } attributeClass)
            return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var userFqn = attributeClass.TypeArguments[0].ToDisplayString(fmt);
        var roleFqn = attributeClass.TypeArguments[1].ToDisplayString(fmt);
        var keyFqn = attributeClass.TypeArguments[2].ToDisplayString(fmt);

        var snakeCase = true;
        string? schema = null;
        string? tablePrefix = null;
        foreach (var namedArgument in ctx.Attributes[0].NamedArguments)
            switch (namedArgument.Key) {
                case "SnakeCase" when namedArgument.Value.Value is bool value:
                    snakeCase = value;
                    break;
                case "Schema" when namedArgument.Value.Value is string schemaValue:
                    schema = schemaValue;
                    break;
                case "TablePrefix" when namedArgument.Value.Value is string prefix:
                    tablePrefix = prefix;
                    break;
            }

        var ns = contextSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "";
        var contextName = contextSymbol.Name;
        var hintName = (ns.Length == 0 ? contextName : ns + "." + contextName).Replace('.', '_');

        var hasGenerateDbSets = contextSymbol.GetAttributes()
            .Any(attribute => attribute.AttributeClass?.ToDisplayString() == GenerateDbSetsAttributeName);

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        if (!hasGenerateDbSets)
            diagnostics.Add(DiagnosticInfo.Create(
                MissingGenerateDbSets, LocationInfo.From(contextSymbol), contextSymbol.ToDisplayString(fmt)));

        return new IdentityTarget(
            ns,
            contextName,
            hintName,
            userFqn,
            roleFqn,
            keyFqn,
            snakeCase,
            schema,
            tablePrefix,
            hasGenerateDbSets,
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, IdentityTarget target) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.EntityFrameworkCore.Identity.Generators.ElarionIdentityGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine();
        if (target.Namespace.Length > 0) {
            sb.AppendLine($"namespace {target.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {target.ContextName}");
        sb.AppendLine("{");
        AppendDbSet(sb, target.UserFqn, "Users");
        AppendDbSet(sb, target.RoleFqn, "Roles");
        AppendDbSet(sb, $"{IdentityNamespace}.IdentityUserClaim<{target.KeyFqn}>", "UserClaims");
        AppendDbSet(sb, $"{IdentityNamespace}.IdentityUserRole<{target.KeyFqn}>", "UserRoles");
        AppendDbSet(sb, $"{IdentityNamespace}.IdentityUserLogin<{target.KeyFqn}>", "UserLogins");
        AppendDbSet(sb, $"{IdentityNamespace}.IdentityRoleClaim<{target.KeyFqn}>", "RoleClaims");
        AppendDbSet(sb, $"{IdentityNamespace}.IdentityUserToken<{target.KeyFqn}>", "UserTokens");
        sb.AppendLine();
        sb.AppendLine(
            "    // Implements the per-feature model-configuration seam the EF DbContext generator calls at the");
        sb.AppendLine(
            "    // end of ConfigureEntities, so it composes with other [GenerateElarion*] features on the same");
        sb.AppendLine(
            "    // context and leaves the neutral OnEntitiesConfigured seam for the host's own configuration.");
        sb.AppendLine($"    partial void {SeamMethodName}(ModelBuilder modelBuilder) =>");
        sb.AppendLine(
            $"        global::Elarion.EntityFrameworkCore.Identity.IdentityModelBuilderExtensions.ApplyElarionIdentity<{target.UserFqn}, {target.RoleFqn}, {target.KeyFqn}>(");
        sb.AppendLine(
            $"            modelBuilder, schema: {SourceLiterals.String(target.Schema)}, " +
            $"tablePrefix: {SourceLiterals.String(target.TablePrefix)}, snakeCase: {(target.SnakeCase ? "true" : "false")});");
        sb.AppendLine("}");

        spc.AddSource($"{target.HintName}.ElarionIdentity.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AppendDbSet(StringBuilder sb, string entityFqn, string propertyName) {
        sb.AppendLine($"    public DbSet<{entityFqn}> {propertyName} => Set<{entityFqn}>();");
    }
}
