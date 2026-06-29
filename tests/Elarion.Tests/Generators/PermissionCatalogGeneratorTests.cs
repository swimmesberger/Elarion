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

    [Fact]
    public void EmitsPerModuleCatalogContributionAndComposes() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static partial class AppModule { }

                [RequirePermission("clients:read")]
                [RequirePermission("clients:write")]
                public sealed class CreateClient { }

                [RequireRole("admin")]
                public sealed class DeleteClient { }
            }
            """;

        var result = Generate(source);
        var extensions = GetGenerated(result, "AppPermissionCatalogExtensions.g.cs");

        extensions.Should().Contain("public static IServiceCollection AddAppPermissions(");
        extensions.Should().Contain("new global::Elarion.Abstractions.Authorization.PermissionCatalogModule");
        extensions.Should().Contain("Module = \"App\",");
        extensions.Should().Contain("Permissions = new string[] { \"clients:read\", \"clients:write\" },");
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
    public void DeduplicatesAndSortsAcrossHandlers() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static partial class AppModule { }

                [RequirePermission("z:write")]
                public sealed class One { }

                [RequirePermission("a:read")]
                [RequirePermission("z:write")]
                public sealed class Two { }
            }
            """;

        var extensions = GetGenerated(Generate(source), "AppPermissionCatalogExtensions.g.cs");

        extensions.Should().Contain("Permissions = new string[] { \"a:read\", \"z:write\" },");
        extensions.Should().Contain("Roles = global::System.Array.Empty<string>(),");
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
                [RequirePermission("orphan:read")]
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

                [RequirePermission("clients:read")]
                public sealed class CreateClient { }
            }
            """;

        var result = RunForDiagnostics(source: source, out var trees);

        result.Should().BeEmpty();
        trees.Should().NotContain(tree => tree.FilePath.EndsWith("PermissionCatalogExtensions.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void IrrelevantEditReusesOutputs() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static partial class AppModule { }

                [RequirePermission("clients:read")]
                public sealed class CreateClient { }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new PermissionCatalogGenerator(),
            source,
            "PermissionCatalogPermissions",
            "PermissionCatalogRoles",
            "PermissionCatalogCombined");
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
            new[]
            {
                new PermissionCatalogGenerator().AsSourceGenerator(),
                new ModuleDefaultServicesGenerator().AsSourceGenerator(),
            },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> RunForDiagnostics(string source) =>
        RunForDiagnostics(source, out _);

    private static IReadOnlyList<Diagnostic> RunForDiagnostics(string source, out IReadOnlyList<SyntaxTree> trees) {
        var ct = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create(
            "PermissionCatalogGeneratorDiagnostics",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new PermissionCatalogGenerator());
        var runResult = driver.RunGenerators(compilation, ct).GetRunResult();
        trees = runResult.GeneratedTrees;
        return runResult.Diagnostics;
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
