using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Tests the module-aware, feature-flag-gated transport mapping emitted by <see cref="AppModuleDiscoveryGenerator"/>:
/// per-module <c>Map{Module}Http</c> / <c>Add{Module}JsonRpc</c> / <c>Add{Module}Mcp</c> / <c>Get{Module}McpMetadata</c>
/// methods, gated aggregates (<c>MapAllEndpoints</c> / <c>RegisterRpcMethods</c> / <c>RegisterMcpMethods</c> /
/// <c>GetMcpMetadata</c>), per-handler transport surface selection, and core modules always mapped. Handlers and
/// modules live in a referenced image (the generator scans references, not the current compilation).
/// </summary>
public sealed class ModuleBootstrapperTransportTests {
    private const string ModulesSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;

        namespace Sample.Billing {
            [AppModule("Billing", Kind = AppModuleKind.Core)]
            public static class BillingModule { }

            [HttpEndpoint("invoices/{id}")]
            public sealed class GetInvoice : IHandler<GetInvoice.Query, Result<GetInvoice.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Number);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("INV"));
            }

            [RpcMethod("invoices.get")]
            public sealed class GetInvoiceRpc : IHandler<GetInvoiceRpc.Query, Result<GetInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Number);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("INV"));
            }

            [RpcMethod("invoices.archive", Transports = RpcTransports.JsonRpc)]
            public sealed class ArchiveInvoiceRpc : IHandler<ArchiveInvoiceRpc.Command, Result<ArchiveInvoiceRpc.Response>> {
                public sealed record Command { public required System.Guid Id { get; init; } }
                public sealed record Response(bool Ok);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(true));
            }

            [RpcMethod("invoices.summarize", Transports = RpcTransports.Mcp)]
            public sealed class SummarizeInvoiceRpc : IHandler<SummarizeInvoiceRpc.Query, Result<SummarizeInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Summary);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("S"));
            }
        }

        namespace Sample.Shipping {
            [AppModule("Shipping")]
            public static class ShippingModule { }

            [HttpEndpoint("shipments")]
            public sealed class CreateShipment : IHandler<CreateShipment.Command, Result<CreateShipment.Response>> {
                public sealed record Command { public required string Address { get; init; } }
                public sealed record Response(System.Guid Id);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
            }

            [RpcMethod("shipments.create")]
            public sealed class CreateShipmentRpc : IHandler<CreateShipmentRpc.Command, Result<CreateShipmentRpc.Response>> {
                public sealed record Command { public required string Address { get; init; } }
                public sealed record Response(System.Guid Id);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
            }
        }
        """;

    private const string HostSource =
        """
        using Elarion.AspNetCore;

        namespace Host;

        [GenerateModuleBootstrapper]
        public static partial class ModuleBootstrapper;
        """;

    [Fact]
    public void Bootstrapper_EmitsPerModuleTransportMethods() {
        var generated = RunGenerator(out _);

        // HTTP + JSON-RPC + MCP per-module methods follow the uniform Map/Add{Module}{Transport} scheme.
        generated.Should().Contain(
            "public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapBillingHttp(");
        generated.Should().Contain(
            "public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapShippingHttp(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher AddBillingJsonRpc(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher AddShippingJsonRpc(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher AddBillingMcp(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher AddShippingMcp(");
        generated.Should().Contain(
            "global::Elarion.JsonRpc.Mcp.RpcMcpMethodMetadata[] GetBillingMcpMetadata()");

        // The per-module HTTP method maps that module's [HttpEndpoint] handler.
        generated.Should().Contain("app.MapGet(\"invoices/{id}\",");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Billing.GetInvoice.Query request");
        // The per-module RPC method registers that module's [RpcMethod] handler.
        generated.Should().Contain(
            "dispatcher.MapHandler<global::Sample.Billing.GetInvoiceRpc.Query, global::Sample.Billing.GetInvoiceRpc.Response>(\"invoices.get\");");
    }

    [Fact]
    public void Bootstrapper_GatesFeatureModulesButNotCore() {
        var generated = RunGenerator(out _);

        // Core module (Billing) is mapped unconditionally on every transport — never wrapped in IsModuleEnabled.
        generated.Should().Contain("        MapBillingHttp(endpoints);");
        generated.Should().Contain("        AddBillingJsonRpc(dispatcher);");
        generated.Should().Contain("        AddBillingMcp(dispatcher);");
        generated.Should().NotContain("if (IsModuleEnabled(configuration, \"Billing\"))");

        // Feature module (Shipping) is gated by its feature flag on every transport.
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Shipping\"))");
        generated.Should().Contain("MapShippingHttp(endpoints);");
        generated.Should().Contain("AddShippingJsonRpc(dispatcher);");
        generated.Should().Contain("AddShippingMcp(dispatcher);");
    }

    [Fact]
    public void Bootstrapper_EmitsGatedDispatcherAggregates() {
        var generated = RunGenerator(out _);

        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher RegisterRpcMethods(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.JsonRpcDispatcher RegisterMcpMethods(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.Mcp.IRpcMcpMetadataSource GetMcpMetadata(");
        generated.Should().Contain(
            "global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        generated.Should().Contain("return dispatcher;");

        // The MCP aggregates compose the per-module MCP registration and metadata.
        generated.Should().Contain("AddBillingMcp(dispatcher);");
        generated.Should().Contain("methods.AddRange(GetBillingMcpMetadata());");
    }

    [Fact]
    public void Bootstrapper_SelectsTransportSurfacesPerHandler() {
        var generated = RunGenerator(out _);

        var jsonRpc = Slice(generated, "JsonRpcDispatcher AddBillingJsonRpc(");
        var mcp = Slice(generated, "JsonRpcDispatcher AddBillingMcp(");
        var mcpMetadata = Slice(generated, "RpcMcpMethodMetadata[] GetBillingMcpMetadata()");

        // A "both" handler is on both dispatchers; a JSON-RPC-only handler is only on /rpc; an MCP-only handler only on MCP.
        jsonRpc.Should().Contain("\"invoices.get\"").And.Contain("\"invoices.archive\"");
        jsonRpc.Should().NotContain("\"invoices.summarize\"");

        mcp.Should().Contain("\"invoices.get\"").And.Contain("\"invoices.summarize\"");
        mcp.Should().NotContain("\"invoices.archive\"");

        // The MCP tool table mirrors the MCP dispatcher surface.
        mcpMetadata.Should().Contain("MethodName = \"invoices.get\"")
            .And.Contain("MethodName = \"invoices.summarize\"");
        mcpMetadata.Should().NotContain("invoices.archive");
    }

    [Fact]
    public void Bootstrapper_GeneratedCodeCompiles() {
        RunGenerator(out var compilationWithGenerated);

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_IsDeterministic() {
        var first = RunGenerator(out _);
        var second = RunGenerator(out _);

        second.Should().Be(first);
    }

    // Extracts a single emitted method body (from its signature marker to the matching closing brace) so a test can
    // assert which transport surface a handler landed on without being confused by call sites elsewhere in the file.
    private static string Slice(string source, string signatureMarker) {
        var start = source.IndexOf(signatureMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the emitted source should contain {0}", signatureMarker);

        var braceStart = source.IndexOf('{', start);
        braceStart.Should().BeGreaterThanOrEqualTo(0);

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++) {
            if (source[i] == '{') {
                depth++;
            } else if (source[i] == '}') {
                depth--;
                if (depth == 0) {
                    return source.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        return source[braceStart..];
    }

    private static string RunGenerator(out Compilation compilationWithGenerated) {
        var modulesReference = CompileToImage(ModulesSource, "Sample.Modules");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(HostSource, parseOptions);
        var references = CreateMetadataReferences().Append(modulesReference).ToArray();
        var hostCompilation = CSharpCompilation.Create(
            "Host",
            [hostTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AppModuleDiscoveryGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            hostCompilation, out compilationWithGenerated, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult().GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), "ModuleBootstrapper.g.cs", StringComparison.Ordinal))
            .GetText()
            .ToString();
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
