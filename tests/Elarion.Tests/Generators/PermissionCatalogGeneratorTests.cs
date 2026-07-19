using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class PermissionCatalogGeneratorTests {
    private const string Preamble =
        """
        using System;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string AppSource = Preamble +
                                     """

                                     namespace Sample.App {
                                         [AppModule("App")]
                                         public static partial class AppModule { }

                                         [RequirePermission("clients", Verbs.Read)]
                                         [RequirePermission("clients", Verbs.Write)]
                                         public sealed class CreateClient { }

                                         [RequireRole("admin")]
                                         public sealed class DeleteClient { }
                                     }
                                     """;

    [Fact]
    public void EmitsPerModuleCatalogContributionWithResourceVerbAndComposes() {
        var result = Generate(AppSource);
        var extensions = GetGenerated(result, "AppPermissionCatalogExtensions.g.cs");

        extensions.Should().Contain("public static IServiceCollection AddAppPermissions(");
        extensions.Should().Contain("new global::Elarion.Abstractions.Authorization.PermissionCatalogModule");
        extensions.Should().Contain("Module = \"App\",");
        extensions.Should().Contain(
            "new global::Elarion.Abstractions.Authorization.PermissionCatalogEntry { Permission = \"clients.read\", Resource = \"clients\", Verb = \"read\" },");
        extensions.Should().Contain(
            "new global::Elarion.Abstractions.Authorization.PermissionCatalogEntry { Permission = \"clients.write\", Resource = \"clients\", Verb = \"write\" },");
        extensions.Should().Contain("Roles = new string[] { \"admin\" },");

        // The ConfigureDefaultServices filler wires it into the module aggregation (the new AddPermissions hook).
        result.GeneratedTrees
            .Select(tree => tree.GetText().ToString())
            .Should()
            .Contain(text =>
                text.Contains("static partial void AddPermissions", StringComparison.Ordinal) &&
                text.Contains("AddAppPermissions(services)", StringComparison.Ordinal));
    }

    [Fact]
    public void EmitsElarionPermissionsStaticWithKubernetesAxesAndTypedAccessors() {
        var permissions = GetGenerated(Generate(AppSource), "ElarionPermissions.g.cs");

        permissions.Should().Contain("public static partial class ElarionPermissions");
        permissions.Should().Contain(
            "public static global::System.Collections.Generic.IReadOnlyList<string> All { get; } = new string[] { \"clients.read\", \"clients.write\" };");
        permissions.Should().Contain(
            "public static global::System.Collections.Generic.IReadOnlyList<string> Roles { get; } = new string[] { \"admin\" };");
        permissions.Should().Contain("[\"App\"] = new string[] { \"clients.read\", \"clients.write\" },"); // ByModule
        permissions.Should()
            .Contain("[\"clients\"] = new string[] { \"clients.read\", \"clients.write\" },"); // ByResource
        permissions.Should().Contain("[\"read\"] = new string[] { \"clients.read\" },"); // ByVerb
        permissions.Should().Contain("[\"write\"] = new string[] { \"clients.write\" },");

        // Typed accessors: resource becomes a nested class, verb a const member.
        permissions.Should().Contain("public static class Clients");
        permissions.Should().Contain("public const string Read = \"clients.read\";");
        permissions.Should().Contain("public const string Write = \"clients.write\";");
    }

    [Fact]
    public void StaticAllDeduplicatesAndSortsAcrossHandlers() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }

                                  [RequirePermission("z", "write")]
                                  public sealed class One { }

                                  [RequirePermission("a", "read")]
                                  [RequirePermission("z", "write")]
                                  public sealed class Two { }
                              }
                              """;

        var permissions = GetGenerated(Generate(source), "ElarionPermissions.g.cs");

        permissions.Should().Contain(
            "public static global::System.Collections.Generic.IReadOnlyList<string> All { get; } = new string[] { \"a.read\", \"z.write\" };");
    }

    [Fact]
    public void ReportsElperm001WhenRequirementNotInAnyModule() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }
                              }

                              namespace Sample.Outside {
                                  [RequirePermission("orphan", "read")]
                                  public sealed class OrphanHandler { }
                              }
                              """;

        var diagnostics = RunForDiagnostics(source);

        diagnostics.Any(d => d.Id == "ELPERM001" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void EmitsNothingWithoutTrigger() {
        const string source =
            """
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Modules;

            namespace Sample.App {
                [AppModule("App")]
                public static partial class AppModule { }

                [RequirePermission("clients", Verbs.Read)]
                public sealed class CreateClient { }
            }
            """;

        var trees = RunForTrees(source);

        trees.Should().NotContain(tree =>
            tree.FilePath.EndsWith("PermissionCatalogExtensions.g.cs", StringComparison.Ordinal));
        trees.Should().NotContain(tree => tree.FilePath.EndsWith("ElarionPermissions.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void IrrelevantEditReusesOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new PermissionCatalogGenerator(),
            AppSource,
            "PermissionCatalogPermissions",
            "PermissionCatalogRoles",
            "PermissionCatalogPerModule",
            "PermissionCatalogStatic");
    }

    private static GeneratorDriverRunResult Generate(string source) {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PermissionCatalogGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The ConfigureDefaultServices skeleton (ModuleDefaultServicesGenerator) declares the partial hooks this
        // generator's filler implements, so both run together.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] {
                new PermissionCatalogGenerator().AsSourceGenerator(),
                new ModuleDefaultServicesGenerator().AsSourceGenerator()
            },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> RunForDiagnostics(string source) {
        return Run(source).Diagnostics;
    }

    private static IReadOnlyList<SyntaxTree> RunForTrees(string source) {
        return Run(source).GeneratedTrees;
    }

    private static GeneratorDriverRunResult Run(string source) {
        var ct = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create(
            "PermissionCatalogGeneratorDiagnostics",
            [
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
                    cancellationToken: ct)
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new PermissionCatalogGenerator());
        return driver.RunGenerators(compilation, ct).GetRunResult();
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
