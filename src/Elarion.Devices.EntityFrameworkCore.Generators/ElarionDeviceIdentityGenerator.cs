using System.Collections.Immutable;
using System.Text;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Devices.EntityFrameworkCore.Generators;

/// <summary>
/// Fills every partial <c>DbContext</c> annotated with <c>[GenerateElarionDeviceIdentity]</c> with the
/// <c>DbSet&lt;DeviceKeyEntity&gt;</c>/<c>DbSet&lt;DevicePairingCodeEntity&gt;</c> properties and an
/// implementation of the EF generator's per-feature model-configuration seam that calls
/// <c>UseElarionDeviceIdentity</c>. So the host writes neither the DbSets nor the table mappings —
/// mirroring <c>[GenerateElarionSettings]</c>/<c>[GenerateElarionActorSnapshots]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ElarionDeviceIdentityGenerator : IIncrementalGenerator {
    private const string AttributeMetadataName = "Elarion.Devices.EntityFrameworkCore.GenerateElarionDeviceIdentityAttribute";
    private const string GenerateDbSetsAttributeName = ElarionGeneratorConventions.GenerateDbSetsAttribute;
    private const string DevicesEntityFrameworkCoreNamespace = "global::Elarion.Devices.EntityFrameworkCore";

    // The per-feature seam DbContextGenerator declares for this attribute — both sides derive the name from the
    // same convention, so they cannot drift.
    private static readonly string SeamMethodName =
        ElarionGeneratorConventions.ModelConfigurationSeamName("GenerateElarionDeviceIdentityAttribute");

    private static readonly DiagnosticDescriptor MissingGenerateDbSets = new(
        "ELDEV001",
        "[GenerateElarionDeviceIdentity] requires [GenerateDbSets]",
        "Context '{0}' is annotated with [GenerateElarionDeviceIdentity] but not [GenerateDbSets]; add [GenerateDbSets] "
        + "so the device identity DbSets and model-configuration seam are generated",
        "Elarion.Devices.EntityFrameworkCore",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetTarget(ctx))
            .Where(static target => target is not null)
            .WithTrackingName("DeviceIdentityTargets");

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

    private sealed record DeviceIdentityTarget(
        string Namespace,
        string ContextName,
        string HintName,
        bool SnakeCase,
        string? KeyTableName,
        string? PairingCodeTableName,
        string? Schema,
        bool Emit,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private static DeviceIdentityTarget? GetTarget(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol contextSymbol) {
            return null;
        }

        var snakeCase = true;
        string? keyTableName = null;
        string? pairingCodeTableName = null;
        string? schema = null;
        if (ctx.Attributes.Length > 0) {
            foreach (var namedArgument in ctx.Attributes[0].NamedArguments) {
                switch (namedArgument.Key) {
                    case "SnakeCase" when namedArgument.Value.Value is bool value:
                        snakeCase = value;
                        break;
                    case "KeyTableName" when namedArgument.Value.Value is string table:
                        keyTableName = table;
                        break;
                    case "PairingCodeTableName" when namedArgument.Value.Value is string table:
                        pairingCodeTableName = table;
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

        return new DeviceIdentityTarget(
            ns,
            contextName,
            hintName,
            snakeCase,
            keyTableName,
            pairingCodeTableName,
            schema,
            Emit: hasGenerateDbSets,
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext spc, DeviceIdentityTarget target) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Devices.EntityFrameworkCore.Generators.ElarionDeviceIdentityGenerator");
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
        sb.AppendLine($"    public DbSet<{DevicesEntityFrameworkCoreNamespace}.DeviceKeyEntity> DeviceKeys => Set<{DevicesEntityFrameworkCoreNamespace}.DeviceKeyEntity>();");
        sb.AppendLine();
        sb.AppendLine($"    public DbSet<{DevicesEntityFrameworkCoreNamespace}.DevicePairingCodeEntity> DevicePairingCodes => Set<{DevicesEntityFrameworkCoreNamespace}.DevicePairingCodeEntity>();");
        sb.AppendLine();
        sb.AppendLine("    // Implements the per-feature model-configuration seam the EF DbContext generator calls at the");
        sb.AppendLine("    // end of ConfigureEntities, so it composes with [GenerateElarionSettings]/[GenerateElarionActorSnapshots].");
        sb.AppendLine($"    partial void {SeamMethodName}(ModelBuilder modelBuilder) =>");
        sb.AppendLine($"        {DevicesEntityFrameworkCoreNamespace}.DeviceIdentityModelBuilderExtensions.UseElarionDeviceIdentity(");
        sb.AppendLine(
            $"            modelBuilder, keyTableName: {SourceLiterals.String(target.KeyTableName)}, " +
            $"pairingCodeTableName: {SourceLiterals.String(target.PairingCodeTableName)}, " +
            $"schema: {SourceLiterals.String(target.Schema)}, snakeCase: {(target.SnakeCase ? "true" : "false")});");
        sb.AppendLine("}");

        spc.AddSource($"{target.HintName}.ElarionDeviceIdentity.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }
}
