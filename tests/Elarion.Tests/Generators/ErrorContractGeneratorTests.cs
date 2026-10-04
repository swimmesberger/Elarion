using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ErrorContractGeneratorTests {
    private const string Header =
        """
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Idempotency;

        namespace Sample.Errors;

        public sealed record TokenProblem(string Reason);

        """;

    private static string Handler(string attributes, string response = "public sealed record Response(string Name);") =>
        Header
        + $$"""
            {{attributes}}
            [Handler("tokens.redeem")]
            public sealed class Redeem : IHandler<Redeem.Command, Result<Redeem.Response>> {
                public sealed record Command(string Token);
                {{response}}
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(AppError.NotFound("x"));
            }
            """;

    [Fact]
    public void DeclaredErrors_ArePublishedInTheManifest_WithKindAndPayload() {
        var generated = Run(Handler(
            """
            [ProducesError("token.malformed", ErrorKind.Validation, typeof(TokenProblem))]
            [ProducesError(ErrorKind.NotFound)]
            """), out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        generated.Should().Contain("token.malformed").And.Contain("global::Sample.Errors.TokenProblem")
            .And.Contain("not_found").And.Contain("NotFound");
    }

    [Fact]
    public void IdempotentHandler_ImpliesTheIdempotencyErrors() {
        var generated = Run(Handler("[Idempotent]"), out _);

        generated.Should().Contain("idempotency.key_required")
            .And.Contain("idempotency.in_progress").And.Contain("idempotency.key_reused");
    }

    [Fact]
    public void RequirePermission_ImpliesUnauthorizedAndForbidden() {
        var generated = Run(Handler("[Elarion.Abstractions.Authorization.RequirePermission(\"a\", \"read\")]"), out _);

        generated.Should().Contain("unauthorized").And.Contain("forbidden");
    }

    [Fact]
    public void InvalidErrorCode_IsReported() {
        Run(Handler("[ProducesError(\"Token.Bad\", ErrorKind.Validation)]"), out var diagnostics);

        diagnostics.Should().ContainSingle(d => d.Id == "ELERR001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ConflictingDeclarationsOfOneCode_AreReported() {
        Run(Handler(
            """
            [ProducesError("seat.taken", ErrorKind.Conflict)]
            [ProducesError("seat.taken", ErrorKind.BusinessRule)]
            """), out var diagnostics);

        diagnostics.Should().ContainSingle(d => d.Id == "ELERR002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ResponseMember_NonNullableWithIgnoreCondition_IsReported() {
        Run(Handler(
            "",
            """
            public sealed record Response(string Name) {
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                public int Count { get; init; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string Required { get; init; } = "";
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string? Fine { get; init; }
            }
            """), out var diagnostics);

        diagnostics.Where(d => d.Id == "ELRPC004").Select(d => d.GetMessage())
            .Should().HaveCount(2)
            .And.Contain(m => m.Contains("Response.Count"))
            .And.Contain(m => m.Contains("Response.Required"));
    }

    [Fact]
    public void ErrorContractOutput_IsDeterministic_AndCachedAfterAnIrrelevantEdit() {
        var source = Handler(
            """
            [ProducesError("b.two", ErrorKind.Conflict)]
            [ProducesError("a.one", ErrorKind.Conflict)]
            """);
        Run(source, out _).Should().Be(Run(source, out _));

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionManifestGenerator(), source, "ManifestRpcMethods", "Manifest");
    }

    [Fact]
    public void KindOnlyDeclaration_UsesTheRuntimeDefaultCodeOfEveryKind() {
        foreach (var kind in Enum.GetValues<ErrorKind>()) {
            var generated = Run(Handler($"[ProducesError(ErrorKind.{kind})]"), out _);

            generated.Should().Contain(ErrorCodes.ForKind(kind), kind.ToString());
        }
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("a.b_c", true)]
    [InlineData("v2.x9", true)]
    [InlineData("Ab", false)]
    [InlineData("a..b", false)]
    [InlineData("1a", false)]
    [InlineData("a-b", false)]
    [InlineData("a.", false)]
    public void ErrorCodeFormat_MatchesTheRuntimeRule(string code, bool valid) {
        ErrorCodes.IsValid(code).Should().Be(valid);
        var diagnostics = new List<Diagnostic>();
        Run(Handler($"[ProducesError(\"{code}\", ErrorKind.Conflict)]"), out var found);
        found.Any(d => d.Id == "ELERR001").Should().Be(!valid);
    }

    private static string Run(string source, out IReadOnlyList<Diagnostic> diagnostics) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ErrorContracts",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
        diagnostics = result.Diagnostics;

        return result.GeneratedTrees
            .SingleOrDefault(t => Path.GetFileName(t.FilePath) == "ElarionManifest.g.cs")?.GetText().ToString()
            ?? string.Empty;
    }
}
