using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Identity.Generators;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionIdentityGeneratorTests {
    private const string ContextSource =
        """
        using System;
        using Microsoft.AspNetCore.Identity;
        using Microsoft.EntityFrameworkCore;
        using Elarion.EntityFrameworkCore;
        using Elarion.EntityFrameworkCore.Identity;

        namespace Sample.App {
            public sealed class AppUser : IdentityUser<Guid>;
            public sealed class AppRole : IdentityRole<Guid>;

            [GenerateDbSets]
            [GenerateElarionIdentity<AppUser, AppRole, Guid>]
            public sealed partial class AuthDbContext : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
            }
        }
        """;

    [Fact]
    public void EmitsIdentityDbSetsAndSeamImplementation() {
        var result = RunIdentityGenerator(ContextSource);
        var generated = GetGenerated(result, "Sample_App_AuthDbContext.ElarionIdentity.g.cs");

        generated.Should()
            .Contain("public DbSet<global::Sample.App.AppUser> Users => Set<global::Sample.App.AppUser>();");
        generated.Should().Contain("public DbSet<global::Sample.App.AppRole> Roles");
        generated.Should().Contain("global::Microsoft.AspNetCore.Identity.IdentityUserClaim<global::System.Guid>");
        generated.Should().Contain("global::Microsoft.AspNetCore.Identity.IdentityUserToken<global::System.Guid>");
        // Implements the per-feature seam, NOT the neutral OnEntitiesConfigured (which stays the host's own hook).
        generated.Should().Contain(
            "partial void OnEntitiesConfigured_GenerateElarionIdentity(ModelBuilder modelBuilder)");
        generated.Should().NotContain("partial void OnEntitiesConfigured(ModelBuilder modelBuilder)");
        generated.Should().Contain(
            "global::Elarion.EntityFrameworkCore.Identity.IdentityModelBuilderExtensions.ApplyElarionIdentity"
            + "<global::Sample.App.AppUser, global::Sample.App.AppRole, global::System.Guid>(");
        generated.Should().Contain("snakeCase: true");
    }

    [Fact]
    public void HostCanImplementNeutralSeamAlongsideIdentity() {
        // Regression (H14): the Identity generator must implement a per-feature seam, leaving the neutral
        // OnEntitiesConfigured free for the host — otherwise a host that adds its own OnEntitiesConfigured
        // (e.g. for a global query filter) trips CS0757 (duplicate partial implementation).
        const string source =
            """
            using System;
            using Microsoft.AspNetCore.Identity;
            using Microsoft.EntityFrameworkCore;
            using Elarion.EntityFrameworkCore;
            using Elarion.EntityFrameworkCore.Identity;

            namespace Sample.App {
                public sealed class AppUser : IdentityUser<Guid>;
                public sealed class AppRole : IdentityRole<Guid>;

                [GenerateDbSets]
                [GenerateElarionIdentity<AppUser, AppRole, Guid>]
                public sealed partial class AuthDbContext : DbContext {
                    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
                    partial void OnEntitiesConfigured(ModelBuilder modelBuilder) { /* host's own configuration */ }
                }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionIdentityHostSeam",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new DbContextGenerator().AsSourceGenerator(), new ElarionIdentityGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void HonorsSnakeCaseFalse() {
        var source = ContextSource.Replace(
            "[GenerateElarionIdentity<AppUser, AppRole, Guid>]",
            "[GenerateElarionIdentity<AppUser, AppRole, Guid>(SnakeCase = false)]");

        var generated = GetGenerated(RunIdentityGenerator(source), "Sample_App_AuthDbContext.ElarionIdentity.g.cs");

        generated.Should().Contain("snakeCase: false");
    }

    [Fact]
    public void ReportsElidn001WhenGenerateDbSetsMissing() {
        const string source =
            """
            using System;
            using Microsoft.AspNetCore.Identity;
            using Microsoft.EntityFrameworkCore;
            using Elarion.EntityFrameworkCore.Identity;

            namespace Sample.App {
                public sealed class AppUser : IdentityUser<Guid>;
                public sealed class AppRole : IdentityRole<Guid>;

                [GenerateElarionIdentity<AppUser, AppRole, Guid>]
                public sealed partial class AuthDbContext : DbContext { }
            }
            """;

        var result = RunIdentityGenerator(source);

        result.Diagnostics.Any(d => d.Id == "ELIDN001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ComposesWithEfGeneratorSeamAndCompiles() {
        // Run BOTH generators: the EF generator declares the OnEntitiesConfigured seam, the Identity generator
        // implements it. Compiling source + both generated trees proves the cross-generator partial contract holds.
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ElarionIdentityCompose",
            [CSharpSyntaxTree.ParseText(ContextSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Parse the generated trees with the same language version as the source so the merged compilation is consistent.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new DbContextGenerator().AsSourceGenerator(), new ElarionIdentityGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantEditReusesTargets() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionIdentityGenerator(), ContextSource, "ElarionIdentityTargets");
    }

    private static GeneratorDriverRunResult RunIdentityGenerator(string source) {
        var compilation = CSharpCompilation.Create(
            "ElarionIdentityGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionIdentityGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
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
