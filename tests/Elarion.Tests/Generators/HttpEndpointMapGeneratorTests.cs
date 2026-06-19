using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Tests for <see cref="HttpEndpointMapGenerator"/>, which emits the <c>MapAll</c> body that wires
/// <c>[HttpEndpoint]</c> handlers (discovered in referenced assemblies) into minimal-API endpoints.
/// </summary>
public sealed class HttpEndpointMapGeneratorTests {
    // Handlers live in a *referenced assembly* — this generator scans compilation.References, not source syntax.
    private const string HandlersSource =
        """
        using System.ComponentModel;
        using Elarion.Abstractions;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;

        namespace Sample.Handlers;

        [HttpEndpoint("clients/{id}")]
        [Description("Gets a client by id.")]
        public sealed class GetClient : IHandler<GetClient.Query, Result<GetClient.Response>> {
            public sealed record Query {
                public required System.Guid Id { get; init; }
            }
            public sealed record Response(string Name);
            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Query request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response("n"));
        }

        [HttpEndpoint("clients")]
        public sealed class CreateClient : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
            public sealed record Command {
                public required string Name { get; init; }
            }
            public sealed record Response(System.Guid Id);
            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
        }

        [HttpEndpoint(HttpVerb.Delete, "clients/{id}")]
        public sealed class DeleteClient : IHandler<DeleteClient.Command, Result<DeleteClient.Response>> {
            public sealed record Command {
                public required System.Guid Id { get; init; }
            }
            public sealed record Response;
            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response());
        }

        [HttpEndpoint("clients/{id}/rename")]
        public sealed class RenameClient : IHandler<RenameClient.Command, Result<RenameClient.Response>> {
            public sealed record Command {
                public required System.Guid Id { get; init; }
                [FromHeader(Name = "X-Tenant")]
                public string? Tenant { get; init; }
            }
            public sealed record Response(string Name);
            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response("n"));
        }

        [HttpEndpoint("clients/{id}/avatar")]
        public sealed class UploadAvatar : IHandler<UploadAvatar.Command, Result<UploadAvatar.Response>> {
            public sealed record Command {
                public required System.Guid Id { get; init; }
                public required IFormFile File { get; init; }
            }
            public sealed record Response(string Url);
            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response("u"));
        }

        [HttpEndpoint("clients/{id}/inherited")]
        public sealed class InheritedDtoMembers : IHandler<InheritedDtoMembers.Command, Result<InheritedDtoMembers.Response>> {
            public abstract record BaseCommand {
                [FromHeader(Name = "X-Tenant")]
                public string? Tenant { get; init; }
            }

            public sealed record Command : BaseCommand {
                public required System.Guid Id { get; init; }
            }

            public abstract record BaseResponse {
                public required string Name { get; init; }
            }

            public sealed record Response : BaseResponse;

            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response { Name = "n" });
        }
        """;

    // The trigger class declaring the partial MapAll stub lives in the current compilation.
    private const string HostSource =
        """
        using Elarion.AspNetCore;
        using Microsoft.AspNetCore.Routing;

        namespace Sample.Http;

        [GenerateHttpEndpointMap]
        public static partial class HttpEndpointMap {
            public static partial IEndpointRouteBuilder MapAll(IEndpointRouteBuilder app);
        }
        """;

    [Fact]
    public void MapAll_QueryHandler_EmitsMapGetWithAsParameters() {
        var generated = RunGenerator(HandlersSource, out _);

        generated.Should().Contain("using Elarion.AspNetCore;");
        generated.Should().Contain("using Microsoft.AspNetCore.Builder;");
        generated.Should().Contain("app.MapGet(\"clients/{id}\",");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Handlers.GetClient.Query request");
        generated.Should().Contain(".WithName(\"Sample.Handlers.GetClient\")");
        generated.Should().Contain(".WithDescription(\"Gets a client by id.\")");
        generated.Should().Contain(".Produces<global::Sample.Handlers.GetClient.Response>(200)");
        generated.Should().Contain(".ProducesElarionErrors()");
    }

    [Fact]
    public void MapAll_CommandHandler_EmitsMapPostWithBodyBinding() {
        var generated = RunGenerator(HandlersSource, out _);

        generated.Should().Contain("app.MapPost(\"clients\",");
        // Body-bound: the request is a plain parameter (no [AsParameters]).
        generated.Should().Contain("                global::Sample.Handlers.CreateClient.Command request,");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Mvc.FromServices] global::Elarion.Abstractions.IHandler<"
            + "global::Sample.Handlers.CreateClient.Command, "
            + "global::Elarion.Abstractions.Result<global::Sample.Handlers.CreateClient.Response>> handler");
    }

    [Fact]
    public void MapAll_ExplicitVerbAndEmptyResponse_EmitsMapDeleteWithNoContent() {
        var generated = RunGenerator(HandlersSource, out _);

        generated.Should().Contain("app.MapDelete(\"clients/{id}\",");
        generated.Should().Contain(
            "global::Elarion.AspNetCore.ElarionHttpResults.ToNoContentResult(await handler.HandleAsync(request, ct))");
        generated.Should().Contain(".Produces(204)");
    }

    [Fact]
    public void MapAll_BodyVerbWithBindingAttribute_SwitchesToAsParameters() {
        var generated = RunGenerator(HandlersSource, out _);

        // RenameClient is a Command (POST) but opts into binding via [FromHeader] → [AsParameters] mode.
        generated.Should().Contain("app.MapPost(\"clients/{id}/rename\",");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Handlers.RenameClient.Command request");
    }

    [Fact]
    public void MapAll_FormFileHandler_DisablesAntiforgery() {
        var generated = RunGenerator(HandlersSource, out _);

        generated.Should().Contain("app.MapPost(\"clients/{id}/avatar\",");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Handlers.UploadAvatar.Command request");
        generated.Should().Contain(".DisableAntiforgery()");
    }

    [Fact]
    public void MapAll_InheritedDtoProperties_AffectBindingAndResponseShape() {
        var generated = RunGenerator(HandlersSource, out _);

        var endpoint = Slice(generated, "app.MapPost(\"clients/{id}/inherited\",");

        endpoint.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Handlers.InheritedDtoMembers.Command request");
        endpoint.Should().Contain(
            "global::Elarion.AspNetCore.ElarionHttpResults.ToResult(await handler.HandleAsync(request, ct))");
        endpoint.Should().Contain(".Produces<global::Sample.Handlers.InheritedDtoMembers.Response>(200)");
        endpoint.Should().NotContain("ToNoContentResult");
    }

    [Fact]
    public void MapAll_GeneratedCodeCompilesAgainstFrameworkApis() {
        RunGenerator(HandlersSource, out var compilationWithGenerated);

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void MapAll_IsDeterministic() {
        var first = RunGenerator(HandlersSource, out _);
        var second = RunGenerator(HandlersSource, out _);

        second.Should().Be(first);
    }

    [Fact]
    public void MapAll_WarnsOnDuplicateRoute() {
        const string duplicateHandlers =
            """
            using Elarion.Abstractions;

            namespace Sample.Dup;

            [HttpEndpoint("clients")]
            public sealed class First : IHandler<First.Command, Result<First.Response>> {
                public sealed record Command;
                public sealed record Response;
                public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                    Command request, System.Threading.CancellationToken ct) => default;
            }

            [HttpEndpoint("clients")]
            public sealed class Second : IHandler<Second.Command, Result<Second.Response>> {
                public sealed record Command;
                public sealed record Response;
                public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                    Command request, System.Threading.CancellationToken ct) => default;
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(duplicateHandlers, "Sample.Dup");

        diagnostics.Should().Contain(d => d.Id == "ELHTTP002" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void MapAll_WarnsWhenRequestOrResponseShapeMissing() {
        const string malformed =
            """
            using Elarion.Abstractions;

            namespace Sample.Bad;

            [HttpEndpoint("orphan")]
            public sealed class Orphan {
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(malformed, "Sample.Bad");

        diagnostics.Should().Contain(d => d.Id == "ELHTTP001" && d.Severity == DiagnosticSeverity.Warning);
    }

    private static string RunGenerator(string handlersSource, out Compilation compilationWithGenerated) {
        var driver = BuildDriver(handlersSource, "Sample.Handlers", out var hostCompilation);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            hostCompilation, out compilationWithGenerated, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult().GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), "HttpEndpointMap.g.cs", StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static ImmutableArray<Diagnostic> RunGeneratorDiagnostics(string handlersSource, string handlersAssembly) {
        var driver = BuildDriver(handlersSource, handlersAssembly, out var hostCompilation);
        driver = driver.RunGenerators(hostCompilation, TestContext.Current.CancellationToken);
        return driver.GetRunResult().Diagnostics;
    }

    private static string Slice(string source, string marker) {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the emitted source should contain {0}", marker);

        var nextEndpoint = source.IndexOf("app.Map", start + marker.Length, StringComparison.Ordinal);
        return nextEndpoint < 0 ? source[start..] : source[start..nextEndpoint];
    }

    private static GeneratorDriver BuildDriver(string handlersSource, string handlersAssembly, out Compilation hostCompilation) {
        var handlersReference = CompileToImage(handlersSource, handlersAssembly);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(HostSource, parseOptions);
        var references = CreateMetadataReferences().Append(handlersReference).ToArray();
        hostCompilation = CSharpCompilation.Create(
            "Sample.Http",
            [hostTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return CSharpGeneratorDriver.Create(
            [new HttpEndpointMapGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
    }

    private static MetadataReference CompileToImage(string source, string assemblyName) {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        using var stream = new MemoryStream();
        compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        return MetadataReference.CreateFromImage(stream.ToArray());
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
