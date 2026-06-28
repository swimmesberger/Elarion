using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ElarionManifestGeneratorTests {
    [Fact]
    public void Manifest_EmitsAssemblyMetadataForDiscoveredEntries() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            namespace Sample.Manifest;

            [AppModule("Manifest")]
            public static class ManifestModule { }

            [HttpEndpoint("manifest")]
            [Handler("manifest.get")]
            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                public sealed record Response(string Name);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("manifest"));
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        generated.Should().Contain("Elarion.Manifest.Schema")
            .And.Contain("Elarion.Manifest.Module.v1")
            .And.Contain("Elarion.Manifest.HttpEndpoint.v1")
            .And.Contain("Elarion.Manifest.RpcMethod.v2")
            .And.Contain("AssemblyMetadataAttribute");
    }

    [Fact]
    public void Manifest_WarnsWhenMcpHandlerIsAppliedToJsonRpcOnlyHandler() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            [Handler("manifest.get", Transports = HandlerTransports.JsonRpc)]
            [McpHandler(ToolName = "manifest_get")]
            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query;
                public sealed record Response;
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response());
            }
            """;

        RunGenerator(source, out var diagnostics);

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELMCP003" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Manifest_WarnsWhenHttpEndpointShapeIsMissing() {
        const string source =
            """
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            [HttpEndpoint("orphan")]
            public sealed class Orphan {
            }
            """;

        RunGenerator(source, out var diagnostics);

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELHTTP001" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Manifest_TopLevelRequestResponse_NotNested_IsDiscovered() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            public sealed record GetThingQuery(int Id) : IQuery;
            public sealed record GetThingResponse(string Name);

            [Handler("things.get")]
            [HttpEndpoint("things/{id}")]
            public sealed class GetThing : IHandler<GetThingQuery, Result<GetThingResponse>> {
                public ValueTask<Result<GetThingResponse>> HandleAsync(GetThingQuery request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<GetThingResponse>>(new GetThingResponse("x"));
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        diagnostics.Any(diagnostic => diagnostic.Id is "ELHTTP001" or "ELHTTP004" or "ELRPC002").Should().BeFalse();
        generated.Should().Contain("things.get")
            .And.Contain("Sample.Manifest.GetThingQuery")
            .And.Contain("Sample.Manifest.GetThingResponse");
    }

    [Fact]
    public void Manifest_HttpEndpoint_RequestWithoutMarkerOrVerb_WarnsCannotInferVerb() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            public sealed record PlainRequest(int Id);
            public sealed record PlainResponse(string Name);

            [HttpEndpoint("plain")]
            public sealed class Plain : IHandler<PlainRequest, Result<PlainResponse>> {
                public ValueTask<Result<PlainResponse>> HandleAsync(PlainRequest request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<PlainResponse>>(new PlainResponse("x"));
            }
            """;

        RunGenerator(source, out var diagnostics);

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELHTTP004" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Manifest_HttpEndpoint_ExplicitVerb_NeedsNoMarker() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            public sealed record PlainRequest(int Id);
            public sealed record PlainResponse(string Name);

            [HttpEndpoint(HttpVerb.Put, "plain")]
            public sealed class Plain : IHandler<PlainRequest, Result<PlainResponse>> {
                public ValueTask<Result<PlainResponse>> HandleAsync(PlainRequest request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<PlainResponse>>(new PlainResponse("x"));
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Any(diagnostic => diagnostic.Id == "ELHTTP004").Should().BeFalse();
        generated.Should().Contain("Put");
    }

    [Fact]
    public void Manifest_RpcMethod_NonResultResponse_WarnsMissingShape() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            public sealed record AskQuery(int Id) : IQuery;

            [Handler("ask.now")]
            public sealed class Ask : IHandler<AskQuery, AskQuery> {
                public ValueTask<AskQuery> HandleAsync(AskQuery request, CancellationToken ct) =>
                    ValueTask.FromResult(request);
            }
            """;

        RunGenerator(source, out var diagnostics);

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELRPC002" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    private static string RunGenerator(string source, out IReadOnlyList<Diagnostic> diagnostics) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ManifestProducer",
            [tree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var result = driver.GetRunResult();
        diagnostics = result.Diagnostics;

        var generatedTree = result.GeneratedTrees.SingleOrDefault(
            tree => string.Equals(Path.GetFileName(tree.FilePath), "ElarionManifest.g.cs", StringComparison.Ordinal));

        return generatedTree?.GetText().ToString() ?? string.Empty;
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
