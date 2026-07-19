using AwesomeAssertions;
using Elarion.Devices.EntityFrameworkCore.Generators;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionDeviceIdentityGeneratorTests {
    private const string ContextSource =
        """
        using Microsoft.EntityFrameworkCore;

        namespace Sample.Data;

        [Elarion.EntityFrameworkCore.GenerateDbSets]
        [Elarion.Devices.EntityFrameworkCore.GenerateElarionDeviceIdentity]
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
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionDeviceIdentity.g.cs");

        source.Should().Contain(
            "public DbSet<global::Elarion.Devices.EntityFrameworkCore.DeviceKeyEntity> DeviceKeys => Set<global::Elarion.Devices.EntityFrameworkCore.DeviceKeyEntity>();");
        source.Should().Contain(
            "public DbSet<global::Elarion.Devices.EntityFrameworkCore.DevicePairingCodeEntity> DevicePairingCodes => Set<global::Elarion.Devices.EntityFrameworkCore.DevicePairingCodeEntity>();");
        source.Should()
            .Contain("partial void OnEntitiesConfigured_GenerateElarionDeviceIdentity(ModelBuilder modelBuilder) =>");
        source.Should().Contain("UseElarionDeviceIdentity(");
        source.Should().Contain("keyTableName: null");
        source.Should().Contain("pairingCodeTableName: null");
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
            [Elarion.Devices.EntityFrameworkCore.GenerateElarionDeviceIdentity(
                SnakeCase = false, KeyTableName = "MyKeys", PairingCodeTableName = "MyCodes", Schema = "infra")]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Data_AppDbContext.ElarionDeviceIdentity.g.cs");
        source.Should().Contain("keyTableName: \"MyKeys\"");
        source.Should().Contain("pairingCodeTableName: \"MyCodes\"");
        source.Should().Contain("schema: \"infra\"");
        source.Should().Contain("snakeCase: false");
    }

    [Fact]
    public void MissingGenerateDbSets_ReportsEldev001AndGeneratesNothing() {
        var result = RunGenerator(
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Data;

            [Elarion.Devices.EntityFrameworkCore.GenerateElarionDeviceIdentity]
            public partial class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELDEV001" && d.Severity == DiagnosticSeverity.Error);
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ComposesWithEfGeneratorSeamAndCompiles() {
        // The EF DbContext generator declares the per-feature seam OnEntitiesConfigured_GenerateElarionDeviceIdentity;
        // this generator implements it. Compiling source + both generated trees proves the contract holds.
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionDeviceIdentityGeneratorTestsCompose",
            [CSharpSyntaxTree.ParseText(ContextSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] {
                new DbContextGenerator().AsSourceGenerator(), new ElarionDeviceIdentityGenerator().AsSourceGenerator()
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
            new ElarionDeviceIdentityGenerator(), ContextSource, "DeviceIdentityTargets");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new ElarionDeviceIdentityGenerator(), ContextSource, "DeviceIdentityTargets");
    }

    private static GeneratorDriverRunResult RunGenerator(string source) {
        var compilation = CSharpCompilation.Create(
            "ElarionDeviceIdentityGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionDeviceIdentityGenerator());
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
