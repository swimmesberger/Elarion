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
            [RpcMethod("manifest.get")]
            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
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
            .And.Contain("Elarion.Manifest.RpcMethod.v1")
            .And.Contain("AssemblyMetadataAttribute");
    }

    [Fact]
    public void Manifest_WarnsWhenMcpMethodIsAppliedToJsonRpcOnlyHandler() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            [RpcMethod("manifest.get", Transports = RpcTransports.JsonRpc)]
            [McpMethod(ToolName = "manifest_get")]
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
