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

        result.GeneratedTrees.Should().BeEmpty();
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
