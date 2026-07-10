using AwesomeAssertions;
using Elarion.Actors.PostgreSql.Generators;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionActorSnapshotsGeneratorTests
{
    private const string ContextSource =
        """
        using Microsoft.EntityFrameworkCore;

        namespace Sample.Data;

        [Elarion.EntityFrameworkCore.GenerateDbSets]
        [Elarion.Actors.PostgreSql.GenerateElarionActorSnapshots]
        public partial class AppDbContext : DbContext
        {
            public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
        }
        """;

    [Fact]
    public void EmitsDbSetAndModelConfigurationSeam()
    {
        var result = RunGenerator(ContextSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionActorSnapshots.g.cs");

        source.Should().Contain(
            "public DbSet<global::Elarion.Actors.PostgreSql.ActorSnapshotEntity> ActorSnapshots => Set<global::Elarion.Actors.PostgreSql.ActorSnapshotEntity>();");
        source.Should().Contain("partial void OnEntitiesConfigured_GenerateElarionActorSnapshots(ModelBuilder modelBuilder) =>");
        source.Should().Contain("UseElarionActorSnapshots(");
        source.Should().Contain("tableName: null");
        source.Should().Contain("schema: null");
        source.Should().Contain("snakeCase: true");
    }

    [Fact]
    public void TableSchemaAndSnakeCaseOverrides_AreHonored()
    {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.EntityFrameworkCore.GenerateDbSets]
            [Elarion.Actors.PostgreSql.GenerateElarionActorSnapshots(SnakeCase = false, TableName = "MyActors", Schema = "infra")]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionActorSnapshots.g.cs");
        source.Should().Contain("tableName: \"MyActors\"");
        source.Should().Contain("schema: \"infra\"");
        source.Should().Contain("snakeCase: false");
    }

    [Fact]
    public void MissingGenerateDbSets_ReportsElasn001AndGeneratesNothing()
    {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.Actors.PostgreSql.GenerateElarionActorSnapshots]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELASN001" && d.Severity == DiagnosticSeverity.Error);
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ComposesWithEfGeneratorSeamAndCompiles()
    {
        // The EF DbContext generator declares the per-feature seam OnEntitiesConfigured_GenerateElarionActorSnapshots;
        // this generator implements it. Compiling source + both generated trees proves the contract holds.
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionActorSnapshotsGeneratorTestsCompose",
            [CSharpSyntaxTree.ParseText(ContextSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new DbContextGenerator().AsSourceGenerator(), new ElarionActorSnapshotsGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantEditReusesTargets()
    {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionActorSnapshotsGenerator(), ContextSource, "ActorSnapshotsTargets");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery()
    {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new ElarionActorSnapshotsGenerator(), ContextSource, "ActorSnapshotsTargets");
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "ElarionActorSnapshotsGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionActorSnapshotsGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static void NoErrors(GeneratorDriverRunResult result) =>
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
