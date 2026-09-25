using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.WebPush.EntityFrameworkCore.Generators;

/// <summary>
/// Fills every partial <c>DbContext</c> annotated with <c>[GenerateElarionWebPush]</c> with the
/// <c>DbSet&lt;PushSubscriptionEntity&gt;</c>/<c>DbSet&lt;VapidKeyEntity&gt;</c> properties and an
/// implementation of the EF generator's per-feature model-configuration seam that calls
/// <c>UseElarionWebPush</c>. So the host writes neither the DbSets nor the table mappings —
/// mirroring <c>[GenerateElarionSettings]</c>/<c>[GenerateElarionActorSnapshots]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionWebPushGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName =
        "Elarion.WebPush.EntityFrameworkCore.GenerateElarionWebPushAttribute";

    private const string GenerateDbSetsAttributeName = ElarionGeneratorConventions.GenerateDbSetsAttribute;
    private const string WebPushEntityFrameworkCoreNamespace = "global::Elarion.WebPush.EntityFrameworkCore";

    // The per-feature seam DbContextGenerator declares for this attribute — both sides derive the name from the
    // same convention, so they cannot drift.
    private static readonly string SeamMethodName =
        ElarionGeneratorConventions.ModelConfigurationSeamName("GenerateElarionWebPushAttribute");

    private static readonly DiagnosticDescriptor MissingGenerateDbSets = new(
        "ELWP001",
        "[GenerateElarionWebPush] requires [GenerateDbSets]",
        "Context '{0}' is annotated with [GenerateElarionWebPush] but not [GenerateDbSets]; add [GenerateDbSets] "
        + "so the Web Push DbSets and model-configuration seam are generated",
        "Elarion.WebPush.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .Where(static target => target is not null)
            .WithTrackingName("WebPushTargets");

        context.RegisterSourceOutput(targets, static (spc, target) => {
            if (target is null) return;

            foreach (var diagnostic in target.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (target.Emit) Emit(spc, target);
        });
    }

    private sealed record WebPushTarget(
        string Namespace,
        string ContextName,
        string HintName,
        bool SnakeCase,
        string? SubscriptionTableName,
        string? VapidKeyTableName,
        string? Schema,
        bool Emit,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static WebPushTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol contextSymbol) return null;

        var snakeCase = true;
        string? subscriptionTableName = null;
        string? vapidKeyTableName = null;
        string? schema = null;
        if (ctx.Attributes.Length > 0)
            foreach (var namedArgument in ctx.Attributes[0].NamedArguments)
                switch (namedArgument.Key) {
                    case "SnakeCase" when namedArgument.Value.Value is bool value:
                        snakeCase = value;
                        break;
                    case "SubscriptionTableName" when namedArgument.Value.Value is string table:
                        subscriptionTableName = table;
                        break;
                    case "VapidKeyTableName" when namedArgument.Value.Value is string table:
                        vapidKeyTableName = table;
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

        return new WebPushTarget(
            ns,
            contextName,
            hintName,
            snakeCase,
            subscriptionTableName,
            vapidKeyTableName,
            schema,
            hasGenerateDbSets,
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, WebPushTarget target) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.WebPush.EntityFrameworkCore.Generators.ElarionWebPushGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591 // Generated code is not documented; a consuming project that enables GenerateDocumentationFile must not be warned about it.");
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
            $"    public DbSet<{WebPushEntityFrameworkCoreNamespace}.PushSubscriptionEntity> PushSubscriptions => Set<{WebPushEntityFrameworkCoreNamespace}.PushSubscriptionEntity>();");
        sb.AppendLine();
        sb.AppendLine(
            $"    public DbSet<{WebPushEntityFrameworkCoreNamespace}.VapidKeyEntity> VapidKeys => Set<{WebPushEntityFrameworkCoreNamespace}.VapidKeyEntity>();");
        sb.AppendLine();
        sb.AppendLine(
            "    // Implements the per-feature model-configuration seam the EF DbContext generator calls at the");
        sb.AppendLine(
            "    // end of ConfigureEntities, so it composes with [GenerateElarionSettings]/[GenerateElarionActorSnapshots].");
        sb.AppendLine($"    partial void {SeamMethodName}(ModelBuilder modelBuilder) =>");
        sb.AppendLine(
            $"        {WebPushEntityFrameworkCoreNamespace}.WebPushModelBuilderExtensions.UseElarionWebPush(");
        sb.AppendLine(
            $"            modelBuilder, subscriptionTableName: {SourceLiterals.String(target.SubscriptionTableName)}, " +
            $"vapidKeyTableName: {SourceLiterals.String(target.VapidKeyTableName)}, " +
            $"schema: {SourceLiterals.String(target.Schema)}, snakeCase: {(target.SnakeCase ? "true" : "false")});");
        sb.AppendLine("}");

        spc.AddSource($"{target.HintName}.ElarionWebPush.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
