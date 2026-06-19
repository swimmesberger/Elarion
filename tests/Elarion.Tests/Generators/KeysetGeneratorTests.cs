using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class KeysetGeneratorTests
{
    private const string NpgsqlAssemblyAttribute =
        "[assembly: Elarion.EntityFrameworkCore.UseElarionEntityFrameworkCore(Provider = Elarion.EntityFrameworkCore.EfCoreProvider.Npgsql)]";

    [Fact]
    public void Keyset_CompositeMixedDirection_EmitsOrderingSeekAndCodec()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("-CreatedAt", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                    public string Name { get; set; } = "";
                }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Client.Keyset.g.cs");

        source.Should().Contain(
            "public sealed class ClientKeyset : global::Elarion.EntityFrameworkCore.Paging.IKeysetDefinition<global::Sample.Domain.Client>");
        source.Should().Contain(
            "? source.OrderByDescending(__e => __e.CreatedAt).ThenBy(__e => __e.Id)");
        source.Should().Contain(
            ": source.OrderBy(__e => __e.CreatedAt).ThenByDescending(__e => __e.Id)");
        // CreatedAt descending => forward seeks "less"; Id (Guid) compares via CompareTo.
        source.Should().Contain(
            "return __e => __e.CreatedAt < __key0 || (__e.CreatedAt == __key0 && __e.Id.CompareTo(__key1) > 0);");
        source.Should().Contain(
            "return __e => __e.CreatedAt > __key0 || (__e.CreatedAt == __key0 && __e.Id.CompareTo(__key1) < 0);");
        source.Should().Contain("__writer.WriteDateTime(__rows[__i].Key0);");
        source.Should().Contain("__writer.WriteGuid(__rows[__i].Key1);");
        source.Should().Contain(
            "private static bool TryDecode(string cursor, out global::System.DateTime __key0, out global::System.Guid __key1)");
        source.Should().Contain("if (!reader.TryReadDateTime(out __key0))");
        source.Should().Contain("if (!reader.TryReadGuid(out __key1))");
    }

    [Fact]
    public void Keyset_Npgsql_UniformAscending_EmitsRowValueSeek()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("CreatedAt", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Client.Keyset.g.cs");

        source.Should().Contain(
            "__e => global::Microsoft.EntityFrameworkCore.EF.Functions.GreaterThan(global::System.ValueTuple.Create(__e.CreatedAt, __e.Id), global::System.ValueTuple.Create(__key0, __key1))");
        source.Should().Contain(
            "__e => global::Microsoft.EntityFrameworkCore.EF.Functions.LessThan(global::System.ValueTuple.Create(__e.CreatedAt, __e.Id), global::System.ValueTuple.Create(__key0, __key1))");
        // Forward (GreaterThan) is emitted before backward (LessThan) for an ascending keyset.
        source.IndexOf("GreaterThan", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("LessThan", StringComparison.Ordinal));
        // Row values compare in SQL, so the Guid CompareTo shim is gone.
        source.Should().NotContain(".CompareTo(");
    }

    [Fact]
    public void Keyset_Npgsql_UniformDescending_EmitsLessThanForward()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("-CreatedAt", "-Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Client.Keyset.g.cs");

        source.Should().Contain(
            "__e => global::Microsoft.EntityFrameworkCore.EF.Functions.LessThan(global::System.ValueTuple.Create(__e.CreatedAt, __e.Id), global::System.ValueTuple.Create(__key0, __key1))");
        source.Should().Contain(
            "__e => global::Microsoft.EntityFrameworkCore.EF.Functions.GreaterThan(global::System.ValueTuple.Create(__e.CreatedAt, __e.Id), global::System.ValueTuple.Create(__key0, __key1))");
        // For a descending keyset, forward seeks "less", so LessThan precedes GreaterThan.
        source.IndexOf("LessThan", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("GreaterThan", StringComparison.Ordinal));
    }

    [Fact]
    public void Keyset_Npgsql_MixedDirection_FallsBackToOrOfAnds()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("-CreatedAt", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Client.Keyset.g.cs");

        // Row values can't express mixed directions, so the portable predicate stands.
        source.Should().NotContain("EF.Functions");
        source.Should().Contain(
            "return __e => __e.CreatedAt < __key0 || (__e.CreatedAt == __key0 && __e.Id.CompareTo(__key1) > 0);");
    }

    [Fact]
    public void Keyset_Npgsql_SingleColumn_UsesScalarSeek()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("Sequence")]
                public sealed class Event {
                    public long Sequence { get; set; }
                }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Event.Keyset.g.cs");

        // A single column gains nothing from a row value; keep the scalar comparison.
        source.Should().NotContain("EF.Functions");
        source.Should().Contain("return __e => __e.Sequence > __key0;");
    }

    [Fact]
    public void Keyset_PortableDefault_DoesNotEmitRowValues()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("CreatedAt", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_Client.Keyset.g.cs");

        // Without the Npgsql opt-in, the seek stays provider-neutral.
        source.Should().NotContain("EF.Functions");
        source.Should().Contain(
            "return __e => __e.CreatedAt > __key0 || (__e.CreatedAt == __key0 && __e.Id.CompareTo(__key1) > 0);");
    }

    [Fact]
    public void Keyset_GeneratedSource_CompilesAgainstRuntime()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("CreatedAt", "Id")]
                public sealed class Order {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_Order.Keyset.g.cs");

        // Compile the generated keyset against a clean entity and the real runtime assembly.
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Order {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """;

        CompileErrors(entity, generated).Should().BeEmpty();
    }

    [Fact]
    public void Keyset_GeneratedPagingExtension_OmitsDefinitionAndCompiles()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("CreatedAt", "Id")]
                public sealed class Order {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """);

        NoErrors(result);
        var keyset = GetGeneratedSource(result, "Sample_Domain_Order.Keyset.g.cs");
        var paging = GetGeneratedSource(result, "Sample_Domain_Order.KeysetPaging.g.cs");

        // The convenience overload drops the keyset-definition parameter and supplies it from the
        // generated definition, so callers never name the generated keyset type.
        paging.Should().Contain("namespace Elarion.EntityFrameworkCore.Paging;");
        paging.Should().Contain("public static class OrderKeysetPagingExtensions");
        paging.Should().Contain("this global::System.Linq.IQueryable<global::Sample.Domain.Order> source");
        paging.Should().NotContain("IKeysetDefinition");
        paging.Should().Contain("global::Sample.Domain.OrderKeyset.Definition");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Order {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """;

        // Entity + keyset + convenience extension must all compile against the real runtime.
        CompileErrors(entity, keyset, paging).Should().BeEmpty();
    }

    [Fact]
    public void Keyset_UnknownColumn_ReportsErrorAndGeneratesNothing()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("Missing", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY001");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void Keyset_NullableColumn_ReportsError()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("Score", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public int? Score { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY003");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void Keyset_UnsupportedColumnType_ReportsError()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("Active", "Id")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public bool Active { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void Keyset_IdNotInKeyset_WarnsButStillGenerates()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.EntityFrameworkCore.Paging.Keyset("CreatedAt")]
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY004");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        // The keyset definition and its convenience paging extension are both emitted.
        result.GeneratedTrees.Select(t => Path.GetFileName(t.FilePath))
            .Should().BeEquivalentTo("Sample_Domain_Client.Keyset.g.cs", "Sample_Domain_Client.KeysetPaging.g.cs");
    }

    private static GeneratorDriverRunResult Generate(string testSource, string assemblyAttributes = "")
    {
        var source =
            $$"""
            {{assemblyAttributes}}
            namespace Elarion.EntityFrameworkCore.Paging {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class KeysetAttribute : System.Attribute {
                    public KeysetAttribute(params string[] columns) {
                    }
                }
            }

            namespace Elarion.EntityFrameworkCore {
                public enum EfCoreProvider { Portable = 0, Npgsql = 1 }

                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
                public sealed class UseElarionEntityFrameworkCoreAttribute : System.Attribute {
                    public EfCoreProvider Provider { get; set; }
                }
            }

            {{testSource}}
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "KeysetGeneratorTests",
            [syntaxTree],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new KeysetGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(params string[] sources)
    {
        var compilation = CSharpCompilation.Create(
            "KeysetCompileCheck",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Preview))),
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static void NoErrors(GeneratorDriverRunResult result) =>
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> PlatformReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static IReadOnlyList<MetadataReference> RuntimeReferences()
    {
        var references = PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(
            typeof(Elarion.EntityFrameworkCore.Paging.CursorWriter).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(
            typeof(Elarion.Abstractions.Paging.Page<>).Assembly.Location));
        return references;
    }
}
