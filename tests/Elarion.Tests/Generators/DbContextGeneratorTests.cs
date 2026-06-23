using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.EntityFrameworkCore.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class DbContextGeneratorTests
{
    [Fact]
    public void GenerateDbSets_UnscopedInterface_GeneratesInterfaceAndImplementingContextMembers()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Domain {
                    [Elarion.EntityFrameworkCore.DbEntity]
                    public sealed class Company {
                    }

                    [Elarion.EntityFrameworkCore.DbEntity]
                    public sealed class Invoice {
                    }
                }

                namespace Sample.Application {
                    [Elarion.EntityFrameworkCore.GenerateDbSets]
                    public partial interface IAppDbContext {
                    }
                }

                namespace Sample.Infrastructure {
                    public sealed partial class AppDbContext
                        : Microsoft.EntityFrameworkCore.DbContext, Sample.Application.IAppDbContext {
                    }

                    public sealed class InvoiceConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                        }
                    }

                    public sealed class SchemaOnlyRowConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<SchemaOnlyRow> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<SchemaOnlyRow> builder) {
                        }
                    }

                    public sealed class SchemaOnlyRow {
                    }
                }
                """);

        var result = Generate(source);
        var dbContextSource = GetGeneratedSource(result, "AppDbContext.DbSets.g.cs");
        var dbContextInterfaceSource = GetGeneratedSource(result, "IAppDbContext.DbSets.g.cs");

        dbContextSource.Should().Contain("public DbSet<Company> Companies => Set<Company>();");
        dbContextSource.Should().Contain("public DbSet<Invoice> Invoices => Set<Invoice>();");
        dbContextSource.Should().Contain(
            "new global::Sample.Infrastructure.InvoiceConfiguration().Configure(modelBuilder.Entity<global::Sample.Domain.Invoice>());");
        dbContextSource.Should().Contain(
            "new global::Sample.Infrastructure.SchemaOnlyRowConfiguration().Configure(modelBuilder.Entity<global::Sample.Infrastructure.SchemaOnlyRow>());");
        dbContextInterfaceSource.Should().Contain("DbSet<Company> Companies { get; }");
        dbContextInterfaceSource.Should().Contain("DbSet<Invoice> Invoices { get; }");
    }

    [Fact]
    public void GenerateDbSets_ScopedInterface_FiltersEntitiesAndConfigurations()
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
                    [Elarion.EntityFrameworkCore.DbEntity(Sample.Persistence.PersistenceScopes.Main)]
                    public sealed class Invoice {
                    }

                    [Elarion.EntityFrameworkCore.DbEntity(Sample.Persistence.PersistenceScopes.Chat)]
                    public sealed class ChatSession {
                    }

                    [Elarion.EntityFrameworkCore.DbEntity(
                        Sample.Persistence.PersistenceScopes.Main,
                        Sample.Persistence.PersistenceScopes.Chat)]
                    public sealed class User {
                    }

                    [Elarion.EntityFrameworkCore.DbEntity]
                    public sealed class LegacyGlobalEntity {
                    }
                }

                namespace Sample.Application {
                    [Elarion.EntityFrameworkCore.GenerateDbSets(Sample.Persistence.PersistenceScopes.Main)]
                    public partial interface IMainDbContext {
                    }
                }

                namespace Sample.Infrastructure {
                    public sealed partial class MainDbContext
                        : Microsoft.EntityFrameworkCore.DbContext, Sample.Application.IMainDbContext {
                    }

                    public sealed class InvoiceConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Invoice> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.Invoice> builder) {
                        }
                    }

                    public sealed class ChatSessionConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.ChatSession> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.ChatSession> builder) {
                        }
                    }

                    public sealed class LegacyGlobalEntityConfiguration
                        : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.LegacyGlobalEntity> {
                        public void Configure(Microsoft.EntityFrameworkCore.EntityTypeBuilder<Sample.Domain.LegacyGlobalEntity> builder) {
                        }
                    }
                }
                """);

        var result = Generate(source);
        var dbContextSource = GetGeneratedSource(result, "MainDbContext.DbSets.g.cs");
        var dbContextInterfaceSource = GetGeneratedSource(result, "IMainDbContext.DbSets.g.cs");

        dbContextSource.Should().Contain("public DbSet<Invoice> Invoices => Set<Invoice>();");
        dbContextSource.Should().Contain("public DbSet<User> Users => Set<User>();");
        dbContextSource.Should().NotContain("ChatSessions");
        dbContextSource.Should().NotContain("LegacyGlobalEntities");
        dbContextSource.Should().Contain("InvoiceConfiguration");
        dbContextSource.Should().NotContain("ChatSessionConfiguration");
        dbContextSource.Should().NotContain("LegacyGlobalEntityConfiguration");
        dbContextInterfaceSource.Should().Contain("DbSet<Invoice> Invoices { get; }");
        dbContextInterfaceSource.Should().Contain("DbSet<User> Users { get; }");
        dbContextInterfaceSource.Should().NotContain("ChatSessions");
        dbContextInterfaceSource.Should().NotContain("LegacyGlobalEntities");
    }

    [Fact]
    public void GenerateDbSets_DbContextWithoutGeneratedInterface_DoesNotGenerateContextMembers()
    {
        var source =
            CreateSource(
                """
                namespace Sample.Domain {
                    [Elarion.EntityFrameworkCore.DbEntity]
                    public sealed class Invoice {
                    }
                }

                namespace Sample.Infrastructure {
                    public sealed partial class AppDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    }
                }
                """);

        var result = Generate(source);

        // The DbContext implements no [GenerateDbSets] interface, so no DbSet/context members are generated.
        // (The entity is still advertised via the assembly manifest for cross-assembly discovery.)
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
                [Elarion.EntityFrameworkCore.DbEntity]
                public sealed class Company {
                }

                [Elarion.EntityFrameworkCore.DbEntity]
                public sealed class Invoice {
                }
            }

            namespace Sample.Application {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                public partial interface IAppDbContext {
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new DbContextGenerator(),
            source,
            "EntitiesAndConfigs");
    }

    [Fact]
    public void GenerateDbSets_EntitiesFromReferencedAssembly_DiscoveredViaManifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = CreateMetadataReferences();

        // Assembly A defines the markers + a [DbEntity]. Running the generator emits A's entity manifest;
        // A is then emitted to an image and referenced by B.
        var sourceA = CreateSource(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.DbEntity]
                public sealed class Invoice {
                }
            }
            """);
        var compilationA = CSharpCompilation.Create(
            "AssemblyA",
            [CSharpSyntaxTree.ParseText(sourceA, parseOptions, cancellationToken: ct)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driverA = CSharpGeneratorDriver.Create(new DbContextGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driverA = driverA.RunGeneratorsAndUpdateCompilation(compilationA, out var outputA, out _, ct);

        using var image = new MemoryStream();
        var emitResult = outputA.Emit(image, cancellationToken: ct);
        emitResult.Success.Should().BeTrue(
            string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var referenceA = MetadataReference.CreateFromImage(image.ToArray());

        // Assembly B has no entities of its own; it must discover Invoice solely from A's manifest.
        const string sourceB =
            """
            namespace Sample.Application {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                public partial interface IAppDbContext {
                }
            }

            namespace Sample.Infrastructure {
                public sealed partial class AppDbContext
                    : Microsoft.EntityFrameworkCore.DbContext, Sample.Application.IAppDbContext {
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
    }

    private static string CreateSource(string testSource) =>
        $$"""
        namespace Elarion.EntityFrameworkCore {
            [System.AttributeUsage(System.AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
            public sealed class GenerateDbSetsAttribute : System.Attribute {
                public GenerateDbSetsAttribute(params string[] scopes) {
                }
            }

            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class DbEntityAttribute : System.Attribute {
                public DbEntityAttribute(params string[] scopes) {
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
            }

            public interface IEntityTypeConfiguration<TEntity> {
                void Configure(EntityTypeBuilder<TEntity> builder);
            }

            public sealed class EntityTypeBuilder<TEntity> {
            }
        }

        {{testSource}}
        """;

    private static GeneratorDriverRunResult Generate(string source)
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
        var result = driver.GetRunResult();

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return result;
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
