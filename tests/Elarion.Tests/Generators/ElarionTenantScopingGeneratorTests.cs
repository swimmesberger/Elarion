using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionTenantScopingGeneratorTests {
    [Fact]
    public void TenantScoping_ImplementsTheModelConfigurationSeam() {
        var result = Generate(
            """
            namespace Sample.Data {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                [Elarion.EntityFrameworkCore.GenerateElarionTenantScoping]
                public partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Data_AppDbContext.ElarionTenantScoping.g.cs");

        source.Should().Contain("partial class AppDbContext");
        // The seam name is derived from the attribute by the shared convention, so the two sides cannot drift.
        source.Should().Contain("partial void OnEntitiesConfigured_GenerateElarionTenantScoping(ModelBuilder modelBuilder)");
        source.Should().Contain(
            "global::Elarion.EntityFrameworkCore.MultiTenancy.ElarionTenantScopingModelBuilderExtensions.ApplyElarionTenantScoping(");
        source.Should().Contain("modelBuilder, this);");
    }

    [Fact]
    public void TenantScoping_GlobalNamespaceContext_EmitsWithoutANamespace() {
        var result = Generate(
            """
            [Elarion.EntityFrameworkCore.GenerateDbSets]
            [Elarion.EntityFrameworkCore.GenerateElarionTenantScoping]
            public partial class RootContext : Microsoft.EntityFrameworkCore.DbContext { }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "RootContext.ElarionTenantScoping.g.cs");

        source.Should().NotContain("namespace ");
        source.Should().Contain("partial class RootContext");
    }

    [Fact]
    public void TenantScoping_WithoutGenerateDbSets_ReportsELTEN001AndEmitsNothing() {
        var result = Generate(
            """
            namespace Sample.Data {
                [Elarion.EntityFrameworkCore.GenerateElarionTenantScoping]
                public partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELTEN001");
        // Emitting the seam without the generated ConfigureEntities that calls it would be a dead method.
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void TenantScoping_ReusesOutputsAfterIrrelevantEdit() {
        var source =
            """
            namespace Sample.Data {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                [Elarion.EntityFrameworkCore.GenerateElarionTenantScoping]
                public partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionTenantScopingGenerator(),
            source,
            "TenantScopingTargets");
    }

    private static GeneratorDriverRunResult Generate(string testSource) {
        var syntaxTree = CSharpSyntaxTree.ParseText(testSource, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ElarionTenantScopingGeneratorTests",
            [syntaxTree],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionTenantScopingGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
