using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Settings.EntityFrameworkCore.Generators;

/// <summary>
/// Fills every partial <c>DbContext</c> annotated with <c>[GenerateElarionSettings]</c> with the
/// <c>DbSet&lt;Setting&gt;</c> property and an implementation of the EF generator's per-feature
/// model-configuration seam that calls <c>UseElarionSettings</c>. So the host writes neither the DbSet
/// nor the table mapping — mirroring <c>[GenerateElarionIdempotencyKeys]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionSettingsGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName = "Elarion.Settings.EntityFrameworkCore.GenerateElarionSettingsAttribute";
    private const string GenerateDbSetsAttributeName = ElarionGeneratorConventions.GenerateDbSetsAttribute;
    private const string SettingsNamespace = "global::Elarion.Settings.EntityFrameworkCore";

    // The per-feature seam DbContextGenerator declares for this attribute — both sides derive the name from the
    // same convention, so they cannot drift.
    private static readonly string SeamMethodName =
        ElarionGeneratorConventions.ModelConfigurationSeamName("GenerateElarionSettingsAttribute");

    private static readonly DiagnosticDescriptor MissingGenerateDbSets = new(
        "ELSET001",
        "[GenerateElarionSettings] requires [GenerateDbSets]",
        "Context '{0}' is annotated with [GenerateElarionSettings] but not [GenerateDbSets]; add [GenerateDbSets] "
        + "so the settings DbSet and model-configuration seam are generated",
        "Elarion.Settings.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .Where(static target => target is not null)
            .WithTrackingName("SettingsTargets");

        context.RegisterSourceOutput(targets, static (spc, target) => {
            if (target is null) {
                return;
            }

            foreach (var diagnostic in target.Diagnostics) {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (target.Emit) {
                Emit(spc, target);
            }
        });
    }

    private sealed record SettingsTarget(
        string Namespace,
        string ContextName,
        string HintName,
        bool SnakeCase,
        string? TableName,
        string? Schema,
        bool Emit,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static SettingsTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol contextSymbol) {
            return null;
        }

        var snakeCase = true;
        string? tableName = null;
        string? schema = null;
        if (ctx.Attributes.Length > 0) {
            foreach (var namedArgument in ctx.Attributes[0].NamedArguments) {
                switch (namedArgument.Key) {
                    case "SnakeCase" when namedArgument.Value.Value is bool value:
                        snakeCase = value;
                        break;
                    case "TableName" when namedArgument.Value.Value is string table:
                        tableName = table;
                        break;
                    case "Schema" when namedArgument.Value.Value is string schemaValue:
                        schema = schemaValue;
                        break;
                }
            }
        }

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var ns = contextSymbol.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "";
        var contextName = contextSymbol.Name;
        var hintName = (ns.Length == 0 ? contextName : ns + "." + contextName).Replace('.', '_');

        var hasGenerateDbSets = contextSymbol.GetAttributes()
            .Any(attribute => attribute.AttributeClass?.ToDisplayString() == GenerateDbSetsAttributeName);

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        if (!hasGenerateDbSets) {
            diagnostics.Add(DiagnosticInfo.Create(
                MissingGenerateDbSets, LocationInfo.From(contextSymbol), contextSymbol.ToDisplayString(fmt)));
        }

        return new SettingsTarget(
            ns,
            contextName,
            hintName,
            snakeCase,
            tableName,
            schema,
            Emit: hasGenerateDbSets,
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, SettingsTarget target) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Settings.EntityFrameworkCore.Generators.ElarionSettingsGenerator");
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
        sb.AppendLine($"    public DbSet<{SettingsNamespace}.Setting> Settings => Set<{SettingsNamespace}.Setting>();");
        sb.AppendLine();
        sb.AppendLine("    // Implements the per-feature model-configuration seam the EF DbContext generator calls at the");
        sb.AppendLine("    // end of ConfigureEntities, so it composes with the other [GenerateElarion*] features.");
        sb.AppendLine($"    partial void {SeamMethodName}(ModelBuilder modelBuilder) =>");
        sb.AppendLine($"        {SettingsNamespace}.SettingsModelBuilderExtensions.UseElarionSettings(");
        sb.AppendLine(
            $"            modelBuilder, tableName: {SourceLiterals.String(target.TableName)}, " +
            $"schema: {SourceLiterals.String(target.Schema)}, snakeCase: {(target.SnakeCase ? "true" : "false")});");
        sb.AppendLine("}");

        spc.AddSource($"{target.HintName}.ElarionSettings.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
