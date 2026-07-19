using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Blobs.PostgreSql.Generators;

/// <summary>
/// Fills every partial <c>DbContext</c> annotated with <c>[GenerateElarionBlobStorage]</c> with the
/// <c>DbSet&lt;StoredBlob&gt;</c> property and an implementation of the EF generator's per-feature
/// model-configuration seam that calls <c>UseElarionBlobStorage</c> (mapping both the metadata and the
/// content table; the content row type is internal, so only the metadata DbSet is exposed). So the host
/// writes neither the DbSet nor the table mapping — mirroring <c>[GenerateElarionIdempotencyKeys]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionBlobStorageGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName = "Elarion.Blobs.PostgreSql.GenerateElarionBlobStorageAttribute";
    private const string GenerateDbSetsAttributeName = ElarionGeneratorConventions.GenerateDbSetsAttribute;
    private const string BlobsNamespace = "global::Elarion.Blobs.PostgreSql";

    // The per-feature seam DbContextGenerator declares for this attribute — both sides derive the name from the
    // same convention, so they cannot drift.
    private static readonly string SeamMethodName =
        ElarionGeneratorConventions.ModelConfigurationSeamName("GenerateElarionBlobStorageAttribute");

    private static readonly DiagnosticDescriptor MissingGenerateDbSets = new(
        "ELBLB001",
        "[GenerateElarionBlobStorage] requires [GenerateDbSets]",
        "Context '{0}' is annotated with [GenerateElarionBlobStorage] but not [GenerateDbSets]; add [GenerateDbSets] "
        + "so the blob-storage DbSet and model-configuration seam are generated",
        "Elarion.Blobs.PostgreSql",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .Where(static target => target is not null)
            .WithTrackingName("BlobStorageTargets");

        context.RegisterSourceOutput(targets, static (spc, target) => {
            if (target is null) return;

            foreach (var diagnostic in target.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (target.Emit) Emit(spc, target);
        });
    }

    private sealed record BlobStorageTarget(
        string Namespace,
        string ContextName,
        string HintName,
        bool SnakeCase,
        string? TableName,
        string? ContentTableName,
        string? Schema,
        bool Emit,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static BlobStorageTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol contextSymbol) return null;

        var snakeCase = true;
        string? tableName = null;
        string? contentTableName = null;
        string? schema = null;
        if (ctx.Attributes.Length > 0)
            foreach (var namedArgument in ctx.Attributes[0].NamedArguments)
                switch (namedArgument.Key) {
                    case "SnakeCase" when namedArgument.Value.Value is bool value:
                        snakeCase = value;
                        break;
                    case "TableName" when namedArgument.Value.Value is string table:
                        tableName = table;
                        break;
                    case "ContentTableName" when namedArgument.Value.Value is string contentTable:
                        contentTableName = contentTable;
                        break;
                    case "Schema" when namedArgument.Value.Value is string schemaValue:
                        schema = schemaValue;
                        break;
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
        if (!hasGenerateDbSets)
            diagnostics.Add(DiagnosticInfo.Create(
                MissingGenerateDbSets, LocationInfo.From(contextSymbol), contextSymbol.ToDisplayString(fmt)));

        return new BlobStorageTarget(
            ns,
            contextName,
            hintName,
            snakeCase,
            tableName,
            contentTableName,
            schema,
            hasGenerateDbSets,
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, BlobStorageTarget target) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Blobs.PostgreSql.Generators.ElarionBlobStorageGenerator");
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
        sb.AppendLine(
            $"    public DbSet<{BlobsNamespace}.StoredBlob> StoredBlobs => Set<{BlobsNamespace}.StoredBlob>();");
        sb.AppendLine();
        sb.AppendLine(
            "    // Implements the per-feature model-configuration seam the EF DbContext generator calls at the");
        sb.AppendLine("    // end of ConfigureEntities, so it composes with the other [GenerateElarion*] features.");
        sb.AppendLine($"    partial void {SeamMethodName}(ModelBuilder modelBuilder) =>");
        sb.AppendLine($"        {BlobsNamespace}.PostgreSqlBlobStorageModelBuilderExtensions.UseElarionBlobStorage(");
        sb.AppendLine(
            $"            modelBuilder, tableName: {SourceLiterals.String(target.TableName)}, " +
            $"contentTableName: {SourceLiterals.String(target.ContentTableName)}, " +
            $"schema: {SourceLiterals.String(target.Schema)}, snakeCase: {(target.SnakeCase ? "true" : "false")});");
        sb.AppendLine("}");

        spc.AddSource($"{target.HintName}.ElarionBlobStorage.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
