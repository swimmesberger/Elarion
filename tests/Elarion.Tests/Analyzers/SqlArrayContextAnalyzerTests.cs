using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Sql.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

/// <summary>
/// Covers ELSQL012 (<see cref="SqlArrayContextAnalyzer"/>): a collection interpolated where SQL expects one array
/// value — inside <c>ANY(</c>/<c>ALL(</c>/<c>SOME(</c> or next to <c>@&gt;</c>, <c>&lt;@</c>, <c>&amp;&amp;</c> — is
/// flagged with both correct spellings in the message, while the <c>IN</c> expansion it is designed for, the
/// explicit <c>SqlArray</c> wrapper, scalars, <c>string</c>, and <c>byte[]</c> stay quiet.
/// </summary>
public sealed class SqlArrayContextAnalyzerTests {
    private const string Preamble =
        """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using Elarion.Sql;

        namespace Sample;

        public static class Queries {

        """;

    [Theory]
    [InlineData("""$"SELECT * FROM t WHERE id = ANY({ids})" """, "ANY(")]
    [InlineData("""$"SELECT * FROM t WHERE id = any ( {ids} )" """, "ANY(")]
    [InlineData("""$"SELECT * FROM t WHERE id <> ALL({ids})" """, "ALL(")]
    [InlineData("""$"SELECT * FROM t WHERE id = SOME({ids}::bigint[])" """, "SOME(")]
    [InlineData("""$"SELECT * FROM t WHERE tags @> {ids}" """, "@>")]
    [InlineData("""$"SELECT * FROM t WHERE tags <@ {ids}" """, "<@")]
    [InlineData("""$"SELECT * FROM t WHERE tags && {ids}" """, "&&")]
    [InlineData("""$"SELECT * FROM t WHERE {ids} <@ tags" """, "<@")]
    [InlineData("""$"SELECT * FROM t WHERE {ids}&&tags" """, "&&")]
    public async Task Flags_ACollectionInAnArrayPosition(string interpolation, string construct) {
        var diagnostics = await AnalyzeAsync($$"""
            public static SqlStatement Build(long[] ids) => new({{interpolation}});
            """);

        var flagged = diagnostics.Should().ContainSingle(d => d.Id == "ELSQL012").Subject;
        flagged.Severity.Should().Be(DiagnosticSeverity.Warning);
        flagged.Descriptor.Category.Should().Be("Elarion.Sql");
        flagged.GetMessage().Should().Contain("'ids'").And.Contain($"'{construct}'")
            .And.Contain("IN {collection}").And.Contain("SqlArray.Of(ids)");
        flagged.Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
            .ToString(flagged.Location.SourceSpan).Should().Be("ids");
    }

    [Fact]
    public async Task Flags_EveryCollectionShapeTheHandlerExpands() {
        var diagnostics = await AnalyzeAsync(
            """
            public static SqlStatement FromList(List<Guid> ids) => new($"SELECT 1 WHERE id = ANY({ids})");
            public static SqlStatement FromSequence(IEnumerable<string> names) => new($"SELECT 1 WHERE n = ANY({names})");
            public static SqlStatement FromSet(HashSet<int> ids) => new($"SELECT 1 WHERE id = ANY({ids})");
            public static SqlStatement FromImmutable(ImmutableArray<int> ids) => new($"SELECT 1 WHERE id = ANY({ids})");
            public static SqlStatement FromNullable(ImmutableArray<int>? ids) => new($"SELECT 1 WHERE id = ANY({ids})");
            public static SqlStatement FromNonGeneric(System.Collections.IEnumerable ids) => new($"SELECT 1 WHERE id = ANY({ids})");
            """);

        diagnostics.Where(d => d.Id == "ELSQL012").Should().HaveCount(6);
    }

    [Fact]
    public async Task Flags_AcrossConcatenatedInterpolationsAndRawStrings() {
        var diagnostics = await AnalyzeAsync(
            """"
            public static SqlStatement Concatenated(long[] ids) => new($"SELECT * FROM t " + $"WHERE id = ANY({ids})");

            public static SqlStatement Raw(long[] ids) => new(
                $"""
                 SELECT *
                 FROM t
                 WHERE id = ANY(
                     {ids})
                 """);

            public static SqlWhere Predicate(long[] ids) {
                var where = new SqlWhere();
                where.And($"id = ANY({ids})");
                return where;
            }
            """");

        diagnostics.Where(d => d.Id == "ELSQL012").Should().HaveCount(3);
    }

    [Fact]
    public async Task Ignores_TheInListExpansionAndTheExplicitArrayWrapper() {
        var diagnostics = await AnalyzeAsync(
            """
            public static SqlStatement InList(long[] ids) => new($"SELECT * FROM t WHERE id IN {ids}");
            public static SqlStatement NotInList(List<long> ids) => new($"SELECT * FROM t WHERE id NOT IN {ids}");
            public static SqlStatement Wrapped(long[] ids) => new($"SELECT * FROM t WHERE id = ANY({SqlArray.Of(ids)})");
            public static SqlStatement WrappedAll(List<long> ids) => new($"SELECT * FROM t WHERE id <> ALL({SqlArray.Of(ids)})");
            public static SqlStatement WrappedOperator(string[] tags) => new($"SELECT * FROM t WHERE tags @> {SqlArray.Of(tags)}");
            """);

        diagnostics.Where(d => d.Id == "ELSQL012").Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_ScalarsStringsAndBinaryValuesInArrayPositions() {
        var diagnostics = await AnalyzeAsync(
            """
            public static SqlStatement Scalar(long id) => new($"SELECT * FROM t WHERE {id} = ANY(ids)");
            public static SqlStatement ArrayLiteralText(string ids) => new($"SELECT * FROM t WHERE id = ANY({ids}::bigint[])");
            public static SqlStatement Binary(byte[] payload) => new($"SELECT * FROM t WHERE hashes @> {payload}");
            public static SqlStatement Json(string document) => new($"SELECT * FROM t WHERE doc @> {document}::jsonb");
            public static SqlStatement Fragment(SqlStatement inner) => new($"SELECT * FROM t WHERE id = ANY({inner})");
            public static SqlStatement Untyped(object ids) => new($"SELECT * FROM t WHERE id = ANY({ids})");
            """);

        diagnostics.Where(d => d.Id == "ELSQL012").Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_KeywordsThatOnlyEndInAnyOrAll() {
        var diagnostics = await AnalyzeAsync(
            """
            public static SqlStatement Company(long[] ids) => new($"SELECT * FROM t WHERE company({ids})");
            public static SqlStatement Call(long[] ids) => new($"SELECT * FROM t WHERE my_any({ids})");
            public static SqlStatement Install(long[] ids) => new($"SELECT * FROM t WHERE install({ids})");
            """);

        diagnostics.Where(d => d.Id == "ELSQL012").Should().BeEmpty();
    }

    [Fact]
    public async Task Ignores_OrdinaryInterpolatedStrings() {
        // Only the SQL handler expands collections; a plain string interpolation is somebody else's business.
        var diagnostics = await AnalyzeAsync(
            """
            public static string Text(long[] ids) => $"SELECT * FROM t WHERE id = ANY({ids})";
            """);

        diagnostics.Where(d => d.Id == "ELSQL012").Should().BeEmpty();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string members) {
        var source = Preamble + members + "\n}\n";
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "SqlArrayContextAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new SqlArrayContextAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
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
