using AwesomeAssertions;
using Elarion.Coordination.PostgreSql.Generators;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionRoleLeasesGeneratorTests {
    private const string ContextSource =
        """
        using Microsoft.EntityFrameworkCore;

        namespace Sample.Data;

        [Elarion.EntityFrameworkCore.GenerateDbSets]
        [Elarion.Coordination.PostgreSql.GenerateElarionRoleLeases]
        public partial class AppDbContext : DbContext
        {
            public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
        }
        """;

    [Fact]
    public void EmitsDbSetAndModelConfigurationSeam() {
        var result = RunGenerator(ContextSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionRoleLeases.g.cs");

        source.Should().Contain(
            "public DbSet<global::Elarion.Coordination.PostgreSql.RoleLeaseEntity> RoleLeases => Set<global::Elarion.Coordination.PostgreSql.RoleLeaseEntity>();");
        source.Should()
            .Contain("partial void OnEntitiesConfigured_GenerateElarionRoleLeases(ModelBuilder modelBuilder) =>");
        source.Should().Contain("UseElarionRoleLeases(");
        source.Should().Contain("tableName: null");
        source.Should().Contain("schema: null");
        source.Should().Contain("snakeCase: true");
    }

    [Fact]
    public void TableSchemaAndSnakeCaseOverrides_AreHonored() {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.EntityFrameworkCore.GenerateDbSets]
            [Elarion.Coordination.PostgreSql.GenerateElarionRoleLeases(SnakeCase = false, TableName = "MyLeases", Schema = "infra")]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionRoleLeases.g.cs");
        source.Should().Contain("tableName: \"MyLeases\"");
        source.Should().Contain("schema: \"infra\"");
        source.Should().Contain("snakeCase: false");
    }

    [Fact]
    public void MissingGenerateDbSets_ReportsElrole001AndGeneratesNothing() {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.Coordination.PostgreSql.GenerateElarionRoleLeases]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELROLE001" && d.Severity == DiagnosticSeverity.Error);
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ComposesWithEfGeneratorSeamAndCompiles() {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionRoleLeasesGeneratorTestsCompose",
            [CSharpSyntaxTree.ParseText(ContextSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] {
                new DbContextGenerator().AsSourceGenerator(), new ElarionRoleLeasesGenerator().AsSourceGenerator()
            },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantEditReusesTargets() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionRoleLeasesGenerator(), ContextSource, "RoleLeasesTargets");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new ElarionRoleLeasesGenerator(), ContextSource, "RoleLeasesTargets");
    }

    private static GeneratorDriverRunResult RunGenerator(string source) {
        var compilation = CSharpCompilation.Create(
            "ElarionRoleLeasesGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionRoleLeasesGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
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
