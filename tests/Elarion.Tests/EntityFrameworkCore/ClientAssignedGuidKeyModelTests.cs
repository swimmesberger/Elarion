using System.Reflection;
using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Compiles a <c>[GenerateDbSets]</c> context with <see cref="DbContextGenerator"/> against the <em>real</em>
/// EF Core assemblies and inspects the built model: the generated <c>ApplyElarionClientAssignedGuidKeys</c>
/// pass must declare the discovered domain entities' single-property Guid keys <c>ValueGenerated.Never</c>
/// (navigation-discovered children included), while explicit configuration, store defaults, custom value
/// generators, non-Guid keys, and entities from other assemblies keep their configured/conventional
/// generation. This is the compile-and-behave gate for the emitted convention-API code — the text-level
/// generator tests cannot catch a renamed EF metadata member.
/// </summary>
public sealed class ClientAssignedGuidKeyModelTests {
    [Fact]
    public void GeneratedPass_DeclaresDomainGuidKeysClientAssigned_AndRespectsExplicitConfiguration() {
        var assembly = CompileWithGeneratedDbSets();
        using var context = CreateContext(assembly);

        PrimaryKeyGeneration(context, assembly, "Sample.Domain.Parent").Should().Be(ValueGenerated.Never);
        // The navigation-discovered child (no [EntityConfiguration] of its own) is exactly where the
        // generated-key heuristic misreads a new row as an update — it must be covered by the pass.
        PrimaryKeyGeneration(context, assembly, "Sample.Domain.Child").Should().Be(ValueGenerated.Never);

        // Deliberate generation choices always win over the pass.
        PrimaryKeyGeneration(context, assembly, "Sample.Domain.ExplicitOnAdd").Should().Be(ValueGenerated.OnAdd);
        PrimaryKeyGeneration(context, assembly, "Sample.Domain.StoreDefaulted").Should().Be(ValueGenerated.OnAdd);
        PrimaryKeyGeneration(context, assembly, "Sample.Domain.CustomGenerated").Should().Be(ValueGenerated.OnAdd);

        // Non-Guid keys keep the provider's identity generation.
        PrimaryKeyGeneration(context, assembly, "Sample.Domain.IntKeyed").Should().Be(ValueGenerated.OnAdd);

        // An entity mapped from a foreign assembly (here: the test assembly) is outside the discovered
        // domain assemblies, so the pass never reinterprets its key — Identity/framework entities stay safe.
        context.Model.FindEntityType(typeof(ForeignAssemblyEntity))!
            .FindPrimaryKey()!.Properties.Single().ValueGenerated.Should().Be(ValueGenerated.OnAdd);
    }

    private static ValueGenerated PrimaryKeyGeneration(DbContext context, Assembly assembly, string entityTypeName) {
        var clrType = assembly.GetType(entityTypeName);
        clrType.Should().NotBeNull();
        var entityType = context.Model.FindEntityType(clrType!);
        entityType.Should().NotBeNull($"entity '{entityTypeName}' should be part of the model");
        return entityType!.FindPrimaryKey()!.Properties.Single().ValueGenerated;
    }

    private static DbContext CreateContext(Assembly assembly) {
        var contextType = assembly.GetType("Sample.Persistence.ModelDbContext");
        contextType.Should().NotBeNull();

        // Model building needs provider services, not a live connection — the connection string is never used.
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=model-only;Username=unused;Password=unused")
            .Options;
        return (DbContext)Activator.CreateInstance(contextType!, options)!;
    }

    private static Assembly CompileWithGeneratedDbSets() {
        const string source =
            """
            using Microsoft.EntityFrameworkCore;

            namespace Sample.Domain {
                public sealed class Parent {
                    public System.Guid Id { get; set; }
                    public System.Collections.Generic.List<Child> Children { get; set; } = new();
                }

                // No [EntityConfiguration] of its own: discovered through the Parent navigation.
                public sealed class Child {
                    public System.Guid Id { get; set; }
                    public System.Guid ParentId { get; set; }
                }

                public sealed class ExplicitOnAdd {
                    public System.Guid Id { get; set; }
                }

                public sealed class StoreDefaulted {
                    public System.Guid Id { get; set; }
                }

                public sealed class CustomGenerated {
                    public System.Guid Id { get; set; }
                }

                public sealed class IntKeyed {
                    public int Id { get; set; }
                }
            }

            namespace Sample.Persistence {
                [Elarion.EntityFrameworkCore.GenerateDbSets]
                public sealed partial class ModelDbContext : Microsoft.EntityFrameworkCore.DbContext {
                    public ModelDbContext(Microsoft.EntityFrameworkCore.DbContextOptions options) : base(options) {
                    }

                    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder) {
                        // Mapped before ConfigureEntities so it is present while the generated pass runs —
                        // it lives in another assembly and must be left alone.
                        modelBuilder.Entity<Elarion.Tests.EntityFrameworkCore.ForeignAssemblyEntity>();
                        ConfigureEntities(modelBuilder);
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class ParentConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.Parent> {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sample.Domain.Parent> builder) {
                        builder.HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class ExplicitOnAddConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.ExplicitOnAdd> {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sample.Domain.ExplicitOnAdd> builder) {
                        builder.Property(e => e.Id).ValueGeneratedOnAdd();
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class StoreDefaultedConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.StoreDefaulted> {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sample.Domain.StoreDefaulted> builder) {
                        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class CustomGeneratedConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.CustomGenerated> {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sample.Domain.CustomGenerated> builder) {
                        builder.Property(e => e.Id)
                            .HasValueGenerator<Microsoft.EntityFrameworkCore.ValueGeneration.GuidValueGenerator>();
                    }
                }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class IntKeyedConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Sample.Domain.IntKeyed> {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Sample.Domain.IntKeyed> builder) {
                    }
                }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ClientAssignedGuidKeyModelTests.Dynamic",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create(new DbContextGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        using var image = new MemoryStream();
        var emitResult = output.Emit(image, cancellationToken: ct);
        emitResult.Success.Should().BeTrue(
            string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(image.ToArray());
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        // The test host's TPA list contains the real EF Core, Npgsql, and Elarion assemblies (and this test
        // assembly), so the generated code is compiled against exactly what an application compiles against.
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}

/// <summary>Guid-keyed entity owned by the test assembly — foreign to the dynamic compilation above.</summary>
public sealed class ForeignAssemblyEntity {
    public Guid Id { get; set; }
}
