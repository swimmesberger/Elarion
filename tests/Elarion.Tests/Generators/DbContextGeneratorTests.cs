using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.EntityFrameworkCore.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class DbContextGeneratorTests
{
    [Fact]
    public void GenerateDbSets_UnscopedContext_GeneratesDbSetsAndConfigureEntitiesOnTheClass()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Domain {
                    public sealed class Company {
                    }

                    public sealed class Invoice {
                    }

                    public sealed class SchemaOnlyRow {
                    }
                }

                namespace Sample.Infrastructure {
                    [Elarion.EntityFrameworkCore.GenerateDbSets]
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class CompanyConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Company> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Company> builder) {
                        }
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class InvoiceConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                        }
                    }

                    // No [EntityConfiguration]: this configuration is not discovered, so SchemaOnlyRow gets
                    // neither a DbSet nor a Configure call.
                    public sealed class SchemaOnlyRowConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.SchemaOnlyRow> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.SchemaOnlyRow> builder) {
                        }
                    }
                }
                """);

        var result = Generate(source);
        var dbContextSource = GetGeneratedSource(result, "AppDbContext.DbSets.g.cs");

        dbContextSource.Should().Contain("partial class AppDbContext {");
        dbContextSource.Should().Contain("public DbSet<Company> Companies => Set<Company>();");
        dbContextSource.Should().Contain("public DbSet<Invoice> Invoices => Set<Invoice>();");
        dbContextSource.Should().Contain("var __config0 = new global::Sample.Infrastructure.CompanyConfiguration();");
        dbContextSource.Should().Contain("modelBuilder.ApplyConfiguration<global::Sample.Domain.Company>(__config0);");
        dbContextSource.Should().Contain("var __config1 = new global::Sample.Infrastructure.InvoiceConfiguration();");
        dbContextSource.Should().Contain("modelBuilder.ApplyConfiguration<global::Sample.Domain.Invoice>(__config1);");
        dbContextSource.Should().NotContain("SchemaOnlyRow");
        // No interface is generated — the DbSets are a concern of the implementation only.
        result.GeneratedTrees
            .Select(tree => Path.GetFileName(tree.FilePath))
            .Should()
            .NotContain(name => name.StartsWith("I", StringComparison.Ordinal) && name.EndsWith(".DbSets.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateDbSets_ConfigurationImplementsMultipleEntities_GeneratesDbSetAndApplyPerEntity()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Domain {
                    public sealed class Author {
                    }

                    public sealed class Book {
                    }
                }

                namespace Sample.Infrastructure {
                    [Elarion.EntityFrameworkCore.GenerateDbSets]
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class LibraryConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Author>,
                          Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Book> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Author> builder) {
                        }

                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Book> builder) {
                        }
                    }
                }
                """);

        var result = Generate(source);
        var dbContextSource = GetGeneratedSource(result, "AppDbContext.DbSets.g.cs");

        dbContextSource.Should().Contain("public DbSet<Author> Authors => Set<Author>();");
        dbContextSource.Should().Contain("public DbSet<Book> Books => Set<Book>();");
        // One configuration instance, reused across both entities it configures.
        dbContextSource.Should().Contain("var __config0 = new global::Sample.Infrastructure.LibraryConfiguration();");
        dbContextSource.Should().Contain("modelBuilder.ApplyConfiguration<global::Sample.Domain.Author>(__config0);");
        dbContextSource.Should().Contain("modelBuilder.ApplyConfiguration<global::Sample.Domain.Book>(__config0);");
        dbContextSource.Should().NotContain("__config1");
    }

    [Fact]
    public void GenerateDbSets_ScopedContext_FiltersConfigurationsAndEntities()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Persistence {
                    public static class PersistenceScopes {
                        public const string Main = "main";
                        public const string Chat = "chat";
                    }
                }

                namespace Sample.Domain {
                    public sealed class Invoice {
                    }

                    public sealed class ChatSession {
                    }

                    public sealed class User {
                    }

                    public sealed class LegacyGlobalEntity {
                    }
                }

                namespace Sample.Infrastructure {
                    [Elarion.EntityFrameworkCore.GenerateDbSets(Sample.Persistence.PersistenceScopes.Main)]
                    public sealed partial class MainDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration(Sample.Persistence.PersistenceScopes.Main)]
                    public sealed class InvoiceConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                        }
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration(Sample.Persistence.PersistenceScopes.Chat)]
                    public sealed class ChatSessionConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.ChatSession> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.ChatSession> builder) {
                        }
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration(
                        Sample.Persistence.PersistenceScopes.Main,
                        Sample.Persistence.PersistenceScopes.Chat)]
                    public sealed class UserConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.User> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.User> builder) {
                        }
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class LegacyGlobalEntityConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.LegacyGlobalEntity> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.LegacyGlobalEntity> builder) {
                        }
                    }
                }
                """);

        var result = Generate(source);
        var dbContextSource = GetGeneratedSource(result, "MainDbContext.DbSets.g.cs");

        dbContextSource.Should().Contain("public DbSet<Invoice> Invoices => Set<Invoice>();");
        dbContextSource.Should().Contain("public DbSet<User> Users => Set<User>();");
        dbContextSource.Should().NotContain("ChatSessions");
        dbContextSource.Should().NotContain("LegacyGlobalEntities");
        dbContextSource.Should().Contain("InvoiceConfiguration");
        dbContextSource.Should().Contain("UserConfiguration");
        dbContextSource.Should().NotContain("ChatSessionConfiguration");
        dbContextSource.Should().NotContain("LegacyGlobalEntityConfiguration");
    }

    [Fact]
    public void GenerateDbSets_EntityConfigurationWithoutInterface_ReportsDiagnosticAndGeneratesNothing()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Infrastructure {
                    [Elarion.EntityFrameworkCore.GenerateDbSets]
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class NotAConfiguration {
                    }
                }
                """);

        var result = GenerateAllowingDiagnostics(source);

        result.Diagnostics
            .Should()
            .Contain(diagnostic => diagnostic.Id == "ELEFC001" && diagnostic.Severity == DiagnosticSeverity.Warning);
        result.GeneratedTrees
            .Select(tree => Path.GetFileName(tree.FilePath))
            .Should()
            .NotContain(name => name.EndsWith(".DbSets.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateDbSets_ContextWithoutAttribute_DoesNotGenerate()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Domain {
                    public sealed class Invoice {
                    }
                }

                namespace Sample.Infrastructure {
                    // No [GenerateDbSets]: this context is not a target, so nothing is generated for it.
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class InvoiceConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                        }
                    }
                }
                """);

        var result = Generate(source);

        // The context is not marked [GenerateDbSets], so no DbSet/context members are generated.
        // (The configuration is still advertised via the assembly manifest for cross-assembly discovery.)
        result.GeneratedTrees
            .Select(tree => Path.GetFileName(tree.FilePath))
            .Should()
            .NotContain(name => name.EndsWith(".DbSets.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateDbSets_IrrelevantEdit_ReusesCollectedData()
    {
        var source = CreateSource(
            """
            namespace Sample.Domain {
                public sealed class Company {
                }

                public sealed class Invoice {
                }
            }

            namespace Sample.Infrastructure {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class CompanyConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Company> {
                    public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Company> builder) {
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class InvoiceConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                    public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                    }
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new DbContextGenerator(),
            source,
            "Configurations");
    }

    [Fact]
    public void GenerateDbSets_ConfigurationsFromReferencedAssembly_DiscoveredViaManifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = CreateMetadataReferences();

        // Assembly A defines the Elarion markers + a [EntityConfiguration] for Invoice, configuring against
        // the real EF Core types (so it does not re-export a stub Microsoft.EntityFrameworkCore.DbContext that
        // would collide with the real one when B resolves its DbContext base type). Running the generators
        // emits A's configuration manifest (the dedicated EntityConfigurationManifestGenerator owns that
        // emission now); A is then emitted to an image and referenced by B.
        const string sourceA =
            """
            namespace Elarion.EntityFrameworkCore {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class GenerateDbSetsAttribute : System.Attribute {
                    public GenerateDbSetsAttribute(params string[] scopes) { }
                }

                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class EntityConfigurationAttribute : System.Attribute {
                    public EntityConfigurationAttribute(params string[] scopes) { }
                }
            }

            namespace Sample.Domain {
                public sealed class Invoice {
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class InvoiceConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Invoice> {
                    public void Configure(
                        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Invoice> builder) {
                    }
                }
            }
            """;
        var compilationA = CSharpCompilation.Create(
            "AssemblyA",
            [CSharpSyntaxTree.ParseText(sourceA, parseOptions, cancellationToken: ct)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driverA = CSharpGeneratorDriver
            .Create(new DbContextGenerator(), new EntityConfigurationManifestGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driverA = driverA.RunGeneratorsAndUpdateCompilation(compilationA, out var outputA, out _, ct);

        using var image = new MemoryStream();
        var emitResult = outputA.Emit(image, cancellationToken: ct);
        emitResult.Success.Should().BeTrue(
            string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var referenceA = MetadataReference.CreateFromImage(image.ToArray());

        // Assembly B has no configurations of its own; its [GenerateDbSets] context must discover Invoice
        // solely from A's manifest.
        const string sourceB =
            """
            namespace Sample.Infrastructure {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                }
            }
            """;
        var compilationB = CSharpCompilation.Create(
            "AssemblyB",
            [CSharpSyntaxTree.ParseText(sourceB, parseOptions, cancellationToken: ct)],
            [.. references, referenceA],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driverB = CSharpGeneratorDriver.Create(new DbContextGenerator());
        driverB = driverB.RunGenerators(compilationB, ct);

        var generated = string.Concat(driverB.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString()));
        generated.Should().Contain("DbSet<Invoice> Invoices");
        generated.Should().Contain("new global::Sample.Domain.InvoiceConfiguration()");
        generated.Should().Contain("modelBuilder.ApplyConfiguration<global::Sample.Domain.Invoice>(");
    }

    [Fact]
    public void GenerateDbSets_NewSameAssemblyConfigurationAdded_RegeneratesDbSets()
    {
        // Reproduces the IDE scenario the user hit: a new [EntityConfiguration] is added in the same
        // assembly as the [GenerateDbSets] context (a new file), with no rebuild. The pure-Roslyn pipeline
        // must surface the new DbSet on the next incremental run — bounding any remaining "needs a restart"
        // behavior to the IDE source-generator host rather than this generator.
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var initialSource =
            CreateSource(
                """
                namespace Sample.Domain {
                    public sealed class Company {
                    }
                }

                namespace Sample.Infrastructure {
                    [Elarion.EntityFrameworkCore.GenerateDbSets]
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }

                    [Elarion.EntityFrameworkCore.EntityConfiguration]
                    public sealed class CompanyConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Company> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Company> builder) {
                        }
                    }
                }
                """);

        var compilation = CSharpCompilation.Create(
            "DbContextGeneratorTests",
            [CSharpSyntaxTree.ParseText(initialSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DbContextGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            additionalTexts: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, ct);

        // Add a brand-new same-assembly [EntityConfiguration] as a separate syntax tree.
        var addedTree = CSharpSyntaxTree.ParseText(
            """
            namespace Sample.Domain {
                public sealed class Invoice {
                }
            }

            namespace Sample.Infrastructure {
                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class InvoiceConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                    public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                    }
                }
            }
            """,
            parseOptions,
            cancellationToken: ct);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(addedTree), ct);
        var result = driver.GetRunResult();

        var dbContextSource = result.Results
            .SelectMany(runResult => runResult.GeneratedSources)
            .Single(source => string.Equals(source.HintName, "AppDbContext.DbSets.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        dbContextSource.Should().Contain("DbSet<Company> Companies");
        dbContextSource.Should().Contain("DbSet<Invoice> Invoices");

        // The regeneration was driven purely by the syntax path: the reference set did not change, so the
        // MetadataReferencesProvider-backed step stays cached. This isolates same-assembly discovery from the
        // cross-assembly manifest read.
        result.Results.Single().TrackedSteps["ReferencedConfigs"]
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .Should()
            .OnlyContain(reason =>
                reason == IncrementalStepRunReason.Unchanged || reason == IncrementalStepRunReason.Cached);
    }

    [Fact]
    public void EntityConfigurationManifest_IrrelevantEdit_ReusesCollectedData()
    {
        var source = CreateSource(
            """
            namespace Sample.Domain {
                public sealed class Company {
                }
            }

            namespace Sample.Infrastructure {
                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class CompanyConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Company> {
                    public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Company> builder) {
                    }
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new EntityConfigurationManifestGenerator(),
            source,
            "ManifestConfigs");
    }

    private static string CreateSource(string testSource) =>
        $$"""
        namespace Elarion.EntityFrameworkCore {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class GenerateDbSetsAttribute : System.Attribute {
                public GenerateDbSetsAttribute(params string[] scopes) {
                }
            }

            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class EntityConfigurationAttribute : System.Attribute {
                public EntityConfigurationAttribute(params string[] scopes) {
                }
            }
        }

        namespace Microsoft.EntityFrameworkCore {
            public class DbContext {
                protected DbSet<T> Set<T>() => throw null!;
            }

            public sealed class DbSet<T> {
            }

            public sealed class ModelBuilder {
                public EntityTypeBuilder<T> Entity<T>() => throw null!;
                public ModelBuilder ApplyConfiguration<TEntity>(IEntityTypeConfiguration<TEntity> configuration)
                    where TEntity : class => throw null!;
            }

            public interface IEntityTypeConfiguration<TEntity> where TEntity : class {
                void Configure(EntityTypeBuilder<TEntity> builder);
            }

            public sealed class EntityTypeBuilder<TEntity> {
            }
        }

        {{testSource}}
        """;

    private static GeneratorDriverRunResult Generate(string source)
    {
        var result = GenerateAllowingDiagnostics(source);

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return result;
    }

    private static GeneratorDriverRunResult GenerateAllowingDiagnostics(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "DbContextGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DbContextGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
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
