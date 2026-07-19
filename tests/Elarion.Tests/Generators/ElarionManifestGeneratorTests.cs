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
            .And.Contain("Elarion.Manifest.RpcMethod.v1")
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
    public void Manifest_FileResponseHandler_PublishesBothTransportEntries() {
        // A Result<ElarionFile> handler is a first-class citizen on every transport: HTTP streams the download,
        // the name-routed transports carry the canonical base64 envelope — so both entries are published and
        // the caller picks the efficient transport per payload.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace Sample.Manifest;

            public sealed record ExportQuery(string Liste) : IQuery;

            [Handler("exports.get")]
            [HttpEndpoint("exports/{liste}")]
            public sealed class GetExport : IHandler<ExportQuery, Result<ElarionFile>> {
                public ValueTask<Result<ElarionFile>> HandleAsync(ExportQuery request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<ElarionFile>>(
                        new ElarionFile(new byte[] { 1 }, "application/octet-stream"));
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning).Should().BeEmpty();
        generated.Should().Contain("Elarion.Manifest.HttpEndpoint.v1");
        generated.Should().Contain("Elarion.Manifest.RpcMethod.v1");
        generated.Should().Contain("exports.get");
    }

    [Fact]
    public void Manifest_PublishesModuleEndpointsContributors() {
        const string source =
            """
            using Elarion.AspNetCore;
            using Microsoft.AspNetCore.Routing;

            namespace Sample.Manifest;

            [ModuleEndpoints("Billing")]
            public static class BillingWebEndpoints {
                public static void MapEndpoints(IEndpointRouteBuilder endpoints) { }
                public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints) => endpoints;
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        generated.Should().Contain("Elarion.Manifest.ModuleEndpoints.v1")
            .And.Contain("Billing")
            .And.Contain("Sample.Manifest.BillingWebEndpoints");
    }

    [Fact]
    public void Manifest_ModuleEndpointsWithoutHooks_WarnsElmod005AndPublishesNothing() {
        const string source =
            """
            using Elarion.AspNetCore;

            namespace Sample.Manifest;

            [ModuleEndpoints("Billing")]
            public static class BillingWebEndpoints {
                // Instance/arity mismatches are not hooks: nothing here is discoverable.
                public static void MapEndpoints(object first, object second) { }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELMOD005" && diagnostic.Severity == DiagnosticSeverity.Warning);
        generated.Should().NotContain("Elarion.Manifest.ModuleEndpoints.v1");
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

    [Fact]
    public void Manifest_ReferencedAssemblyWithUnsupportedSchema_ReportsElmod003AndSkipsEntries() {
        // Regression (M18): a referenced assembly advertising an Elarion manifest whose schema version this
        // generator does not understand must be skipped LOUDLY (ELMOD003) rather than misparsed — silently
        // dropping its permission entries would weaken authorization.
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // A hand-authored referenced manifest at an unknown schema version "999", carrying one permission entry
        // encoded in the manifest's length-prefixed field format (14:<ns>7:<resource>4:<verb>).
        const string producerSource =
            """
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "999")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Permission.v1", "14:Sample.Widgets7:widgets4:read")]
            """;
        var producer = CSharpCompilation.Create(
            "ReferencedManifestProducer",
            [CSharpSyntaxTree.ParseText(producerSource, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        producer.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        const string consumerSource =
            """
            [assembly: Elarion.AspNetCore.GenerateModuleBootstrapper]

            namespace Elarion.AspNetCore {
                [System.AttributeUsage(System.AttributeTargets.Assembly)]
                public sealed class GenerateModuleBootstrapperAttribute : System.Attribute { }
            }
            """;
        var consumer = CSharpCompilation.Create(
            "ManifestConsumer",
            [CSharpSyntaxTree.ParseText(consumerSource, parseOptions, cancellationToken: ct)],
            [.. CreateMetadataReferences(), producer.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AppModuleDiscoveryGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGenerators(consumer, ct);

        var diagnostics = driver.GetRunResult().Diagnostics;

        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Id == "ELMOD003" && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Manifest_DuplicateModuleNames_ReportsElmod006AndPublishesOneWinner() {
        // Duplicate [AppModule] names either crash a module-keyed generator (duplicate AddSource hint) or emit
        // uncompilable bootstrapper code (CS0111/CS0152). The manifest generator — which always runs — reports
        // the duplicate and publishes only the deterministic winner (ordinal-first by type FQN).
        const string source =
            """
            using Elarion.Abstractions.Modules;

            namespace Alpha {
                [AppModule("Sales")]
                public static class AlphaSalesModule { }
            }

            namespace Beta {
                [AppModule("Sales")]
                public static class BetaSalesModule { }
            }
            """;

        var generated = RunGenerator(source, out var diagnostics);

        diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Id == "ELMOD006" && diagnostic.Severity == DiagnosticSeverity.Error);
        generated.Should().Contain("Alpha.AlphaSalesModule");
        generated.Should().NotContain("Beta.BetaSalesModule");
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

        var generatedTree = result.GeneratedTrees.SingleOrDefault(tree =>
            string.Equals(Path.GetFileName(tree.FilePath), "ElarionManifest.g.cs", StringComparison.Ordinal));

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
