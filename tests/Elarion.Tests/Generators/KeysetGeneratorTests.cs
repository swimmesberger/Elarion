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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                    public string Name { get; set; } = "";
                }

                [Elarion.Paging.Keyset<Client>("-CreatedAt", "Id")]
                public sealed partial class RecentClients { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_RecentClients.Keyset.g.cs");

        // The definition is emitted into the annotated partial class, not a separate {Entity}Keyset type.
        source.Should().Contain(
            "partial class RecentClients : global::Elarion.Paging.IKeysetDefinition<global::Sample.Domain.Client>");
        source.Should().Contain("public static RecentClients Definition { get; } = new();");
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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("CreatedAt", "Id")]
                public sealed partial class ClientSeek { }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ClientSeek.Keyset.g.cs");

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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("-CreatedAt", "-Id")]
                public sealed partial class ClientSeek { }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ClientSeek.Keyset.g.cs");

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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("-CreatedAt", "Id")]
                public sealed partial class ClientSeek { }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ClientSeek.Keyset.g.cs");

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
                public sealed class Event {
                    public long Sequence { get; set; }
                }

                [Elarion.Paging.Keyset<Event>("Sequence")]
                public sealed partial class EventSeek { }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_EventSeek.Keyset.g.cs");

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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("CreatedAt", "Id")]
                public sealed partial class ClientSeek { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ClientSeek.Keyset.g.cs");

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
                public sealed class Order {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Order>("CreatedAt", "Id")]
                public sealed partial class OrderSeek { }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_OrderSeek.Keyset.g.cs");

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
    public void Keyset_MultipleKeysetsOnSameEntity_GenerateDistinctDefinitions()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Post {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                    public string Title { get; set; } = "";
                }

                [Elarion.Paging.Keyset<Post>("-CreatedAt", "-Id")]
                public sealed partial class RecentPosts { }

                [Elarion.Paging.Keyset<Post>("Title", "Id")]
                public sealed partial class PostsByTitle { }
            }
            """);

        NoErrors(result);

        // The same entity yields two independent keyset definitions in two distinct files.
        var recent = GetGeneratedSource(result, "Sample_Domain_RecentPosts.Keyset.g.cs");
        var byTitle = GetGeneratedSource(result, "Sample_Domain_PostsByTitle.Keyset.g.cs");

        recent.Should().Contain(
            "partial class RecentPosts : global::Elarion.Paging.IKeysetDefinition<global::Sample.Domain.Post>");
        byTitle.Should().Contain(
            "partial class PostsByTitle : global::Elarion.Paging.IKeysetDefinition<global::Sample.Domain.Post>");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Post {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                    public string Title { get; set; } = "";
                }
            }
            """;

        // Both definitions, against the same entity, compile against the real runtime.
        CompileErrors(entity, recent, byTitle).Should().BeEmpty();
    }

    [Fact]
    public void Keyset_NonPartialClass_ReportsErrorAndGeneratesNothing()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("CreatedAt", "Id")]
                public sealed class NotPartial { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY005");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void Keyset_UnknownColumn_ReportsErrorAndGeneratesNothing()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Client {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("Missing", "Id")]
                public sealed partial class ClientSeek { }
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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public int? Score { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("Score", "Id")]
                public sealed partial class ClientSeek { }
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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public bool Active { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("Active", "Id")]
                public sealed partial class ClientSeek { }
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
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("CreatedAt")]
                public sealed partial class ClientSeek { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY004");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        // The keyset definition is still emitted.
        result.GeneratedTrees.Select(t => Path.GetFileName(t.FilePath))
            .Should().BeEquivalentTo("Sample_Domain_ClientSeek.Keyset.g.cs");
    }

    [Fact]
    public void Keyset_ConventionalEntityIdNotInKeyset_Warns()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class AuditEntry {
                    public System.Guid AuditEntryId { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<AuditEntry>("CreatedAt")]
                public sealed partial class RecentAudits { }
            }
            """);

        // The PK is named {EntityTypeName}Id, not "Id"; the non-determinism warning must still fire.
        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY004");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void Keyset_KeyAttributedPropertyNotInKeyset_Warns()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class AuditEntry {
                    [System.ComponentModel.DataAnnotations.Key]
                    public System.Guid Reference { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<AuditEntry>("CreatedAt")]
                public sealed partial class RecentAudits { }
            }
            """);

        // A [Key]-annotated property with a non-conventional name still triggers the warning.
        result.Diagnostics.Should().Contain(d => d.Id == "ELKEY004");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void Keyset_ConventionalEntityIdInKeyset_DoesNotWarn()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class AuditEntry {
                    public System.Guid AuditEntryId { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<AuditEntry>("CreatedAt", "AuditEntryId")]
                public sealed partial class RecentAudits { }
            }
            """);

        result.Diagnostics.Should().NotContain(d => d.Id == "ELKEY004");
    }

    [Fact]
    public void Keyset_EmitsIdentityTagAndVerifiesItOnDecode()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Client {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                }

                [Elarion.Paging.Keyset<Client>("CreatedAt", "Id")]
                public sealed partial class ClientSeek { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ClientSeek.Keyset.g.cs");

        source.Should().Contain("private const uint __CursorTag =");
        source.Should().Contain("new global::Elarion.Paging.CursorWriter(__CursorTag)");
        source.Should().Contain("global::Elarion.Paging.CursorReader.TryCreate(cursor, __CursorTag, out var reader)");
        source.Should().Contain("throw new global::Elarion.Paging.MalformedCursorException();");
        // BuildSeek no longer returns a nullable expression.
        source.Should().Contain(
            "public global::System.Linq.Expressions.Expression<global::System.Func<global::Sample.Domain.Client, bool>> BuildSeek(string cursor, bool forward)");
    }

    [Fact]
    public void Keyset_DifferentDefinitions_ProduceDifferentIdentityTags()
    {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Post {
                    public System.Guid Id { get; set; }
                    public System.DateTime CreatedAt { get; set; }
                    public string Title { get; set; } = "";
                }

                [Elarion.Paging.Keyset<Post>("CreatedAt", "Id")]
                public sealed partial class ByDate { }

                [Elarion.Paging.Keyset<Post>("Title", "Id")]
                public sealed partial class ByTitle { }
            }
            """);

        NoErrors(result);
        var byDate = TagOf(GetGeneratedSource(result, "Sample_Domain_ByDate.Keyset.g.cs"));
        var byTitle = TagOf(GetGeneratedSource(result, "Sample_Domain_ByTitle.Keyset.g.cs"));

        byDate.Should().NotBe(byTitle);
    }

    private static string TagOf(string source)
    {
        const string marker = "private const uint __CursorTag = ";
        var start = source.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = source.IndexOf(';', start);
        return source.Substring(start, end - start);
    }

    private static GeneratorDriverRunResult Generate(string testSource, string assemblyAttributes = "")
    {
        var source =
            $$"""
            {{assemblyAttributes}}
            namespace Elarion.Paging {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class KeysetAttribute<TEntity> : System.Attribute where TEntity : class {
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
            typeof(Elarion.Paging.CursorWriter).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(
            typeof(Elarion.Abstractions.Paging.Page<>).Assembly.Location));
        return references;
    }
}
