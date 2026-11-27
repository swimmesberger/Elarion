using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Elarion.WebDav.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionDavStoreGeneratorTests {
    private const string ContextSource =
        """
        using Microsoft.EntityFrameworkCore;

        namespace Sample.Data;

        [Elarion.EntityFrameworkCore.GenerateDbSets]
        [Elarion.WebDav.EntityFrameworkCore.GenerateElarionDavStore]
        public partial class AppDbContext : DbContext
        {
            public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
        }
        """;

    [Fact]
    public void EmitsDbSetsAndModelConfigurationSeam() {
        var result = RunGenerator(ContextSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionDavStore.g.cs");
        source.Should().Contain(
            "public DbSet<global::Elarion.WebDav.EntityFrameworkCore.DavCollectionEntity> ElarionDavCollections => Set<global::Elarion.WebDav.EntityFrameworkCore.DavCollectionEntity>();");
        source.Should().Contain("ElarionDavObjects => Set<global::Elarion.WebDav.EntityFrameworkCore.DavObjectEntity>();");
        source.Should().Contain("ElarionDavChanges => Set<global::Elarion.WebDav.EntityFrameworkCore.DavChangeEntity>();");
        source.Should().Contain("partial void OnEntitiesConfigured_GenerateElarionDavStore(ModelBuilder modelBuilder) =>");
        source.Should().Contain("UseElarionDavStore(");
        source.Should().Contain("schema: null, snakeCase: true");
    }

    [Fact]
    public void SchemaAndSnakeCaseOverrides_AreHonored() {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.EntityFrameworkCore.GenerateDbSets]
            [Elarion.WebDav.EntityFrameworkCore.GenerateElarionDavStore(SnakeCase = false, Schema = "dav")]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        NoErrors(result);
        GetGenerated(result, "Sample_Data_AppDbContext.ElarionDavStore.g.cs").Should().Contain("schema: \"dav\", snakeCase: false");
    }

    [Fact]
    public void MissingGenerateDbSets_ReportsEldav001AndGeneratesNothing() {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.WebDav.EntityFrameworkCore.GenerateElarionDavStore]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELDAV001" && d.Severity == DiagnosticSeverity.Error);
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ComposesWithEfGeneratorSeamAndCompiles() {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionDavStoreGeneratorTestsCompose",
            [CSharpSyntaxTree.ParseText(ContextSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DbContextGenerator().AsSourceGenerator(), new ElarionDavStoreGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantEditReusesTargets() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(new ElarionDavStoreGenerator(), ContextSource, "DavStoreTargets");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(new ElarionDavStoreGenerator(), ContextSource, "DavStoreTargets");
    }

    private static GeneratorDriverRunResult RunGenerator(string source) {
        var compilation = CSharpCompilation.Create(
            "ElarionDavStoreGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionDavStoreGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static void NoErrors(GeneratorDriverRunResult result) =>
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees.Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal)).GetText().ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!.Split(Path.PathSeparator).Select(path => SharedMetadataReferences.FromFile(path)).ToArray();
    }
}
