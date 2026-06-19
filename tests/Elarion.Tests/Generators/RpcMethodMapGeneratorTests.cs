using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Tests for <see cref="RpcMethodMapGenerator"/>, which emits the <c>RegisterAll</c> body that wires
/// referenced <c>[RpcMethod]</c> handlers into a <c>JsonRpcDispatcher</c>.
/// </summary>
public sealed class RpcMethodMapGeneratorTests {
    // Handlers live in a referenced assembly and are consumed from generated Elarion manifest metadata.
    private const string HandlersSource =
        """
        using System.ComponentModel;
        using Elarion.Abstractions;

        namespace Sample.Handlers;

        [RpcMethod("clients.create")]
        [Description("Creates a new client record.")]
        [McpMethod(ToolName = "create_client")]
        public sealed class CreateClient
            : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
            public sealed record Command {
                [Description("Human-readable client name.")]
                public required string DisplayName { get; init; }

                public required string Secret { get; init; }

                // Exercises literal escaping in the generated metadata (quotes, backslash, newline).
                [Description("Notes with \"quotes\", a \\ backslash and a\nnewline.")]
                public string? Notes { get; init; }
            }

            public sealed record Response(System.Guid Id);

            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
        }

        [RpcMethod("admin.purge", Transports = RpcTransports.JsonRpc)]
        public sealed class PurgeEverything
            : IHandler<PurgeEverything.Command, Result<PurgeEverything.Response>> {
            public sealed record Command;

            public sealed record Response;

            public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                Command request, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.ValueTask.FromResult<Result<Response>>(new Response());
        }
        """;

    // The trigger class declaring the partial RegisterAll stub lives in the current compilation.
    private const string HostSource =
        """
        using Elarion.JsonRpc;

        namespace Sample.Rpc;

        [GenerateRpcMethodMap]
        public static partial class RpcMethodMap {
            public static partial JsonRpcDispatcher RegisterAll(JsonRpcDispatcher dispatcher);
        }
        """;

    [Fact]
    public void RegisterAll_EmitsUsingElarionAndTypedMapHandlerCall() {
        var generated = RunGenerator(out _);

        // using Elarion; is required so the generated body resolves the MapHandler extension.
        generated.Should().Contain("using Elarion;");
        generated.Should().Contain("using Elarion.JsonRpc;");
        generated.Should().Contain(
            ".MapHandler<global::Sample.Handlers.CreateClient.Command, global::Sample.Handlers.CreateClient.Response>(\"clients.create\")");
        // JSON-RPC-only handlers are still registered on the /rpc dispatcher.
        generated.Should().Contain(
            ".MapHandler<global::Sample.Handlers.PurgeEverything.Command, global::Sample.Handlers.PurgeEverything.Response>(\"admin.purge\")");
    }

    [Fact]
    public void McpMetadata_EmitsClassDescriptionAndToolNameOverride() {
        var generated = RunGenerator(out _);

        generated.Should().Contain("public static global::Elarion.JsonRpc.Mcp.IRpcMcpMetadataSource McpMetadata()");
        generated.Should().Contain("MethodName = \"clients.create\"");
        generated.Should().Contain("RequestType = typeof(global::Sample.Handlers.CreateClient.Command)");
        generated.Should().Contain("Description = \"Creates a new client record.\"");
        generated.Should().Contain("ToolName = \"create_client\"");
    }

    [Fact]
    public void McpMetadata_EmitsOnlyDescribedParameters() {
        var generated = RunGenerator(out _);

        generated.Should().Contain(
            "new global::Elarion.JsonRpc.Mcp.RpcMcpParameterDescriptor(\"DisplayName\", \"Human-readable client name.\")");
        // A property without [Description] produces no parameter descriptor.
        generated.Should().NotContain("\"Secret\"");
    }

    [Fact]
    public void McpMetadata_ExcludesJsonRpcOnlyMethods() {
        var generated = RunGenerator(out _);

        // The MCP table holds only MCP-surfaced methods; the per-entry Enabled flag no longer exists.
        generated.Should().Contain("MethodName = \"clients.create\"");
        generated.Should().NotContain("MethodName = \"admin.purge\"");
        generated.Should().NotContain("Enabled = ");
    }

    [Fact]
    public void RegisterMcpAll_EmitsOnlyMcpSurfacedHandlers() {
        var generated = RunGenerator(out _);

        generated.Should().Contain("public static JsonRpcDispatcher RegisterMcpAll(");
        // clients.create is on both surfaces → mapped into both the /rpc and MCP dispatchers (twice total).
        generated.Should().Contain(
            ".MapHandler<global::Sample.Handlers.CreateClient.Command, global::Sample.Handlers.CreateClient.Response>(\"clients.create\")",
            Exactly.Times(2));
        // admin.purge is JSON-RPC-only → mapped only by RegisterAll, never into the MCP dispatcher.
        generated.Should().Contain(
            ".MapHandler<global::Sample.Handlers.PurgeEverything.Command, global::Sample.Handlers.PurgeEverything.Response>(\"admin.purge\")",
            Exactly.Times(1));
    }

    [Fact]
    public void RegisterAll_GeneratedCodeCompilesAgainstFrameworkMapHandler() {
        RunGenerator(out var compilationWithGenerated);

        // The generated .MapHandler<...> call must bind to the real Elarion.RpcDispatcherExtensions.
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void McpMetadata_WarnsOnToolNameCollision() {
        const string collidingHandlers =
            """
            using Elarion.Abstractions;

            namespace Sample.Collide;

            [RpcMethod("a.b")]
            public sealed class First : IHandler<First.Command, Result<First.Response>> {
                public sealed record Command;
                public sealed record Response;
                public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                    Command request, System.Threading.CancellationToken ct) => default;
            }

            [RpcMethod("a_b")]
            public sealed class Second : IHandler<Second.Command, Result<Second.Response>> {
                public sealed record Command;
                public sealed record Response;
                public System.Threading.Tasks.ValueTask<Result<Response>> HandleAsync(
                    Command request, System.Threading.CancellationToken ct) => default;
            }
            """;

        // "a.b" and "a_b" both collapse to the tool name "a_b" under the default transform.
        var diagnostics = RunGeneratorDiagnostics(collidingHandlers);

        diagnostics.Should().Contain(d => d.Id == "ELMCP002" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void RegisterAll_IsDeterministic() {
        var first = RunGenerator(out _);
        var second = RunGenerator(out _);

        second.Should().Be(first);
    }

    [Fact]
    public void RegisterAll_ConsumesReferencedAssemblyManifest() {
        var handlersReference = CompileToImage(ManifestOnlySource(), "Sample.Rpc.ManifestOnly");

        var generated = RunGenerator([handlersReference], out var compilationWithGenerated);

        generated.Should().Contain(
            ".MapHandler<global::ManifestOnly.GetManifest.Query, global::ManifestOnly.GetManifest.Response>(\"manifest.get\")",
            Exactly.Times(2));
        generated.Should().Contain("MethodName = \"manifest.get\"");
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void RegisterAll_ConsumesCompilationReferenceManifest() {
        var handlersReference = CompileToCompilationReference(ManifestOnlySource(), "Sample.Rpc.ManifestOnly");

        var generated = RunGenerator([handlersReference], out var compilationWithGenerated);

        generated.Should().Contain(
            ".MapHandler<global::ManifestOnly.GetManifest.Query, global::ManifestOnly.GetManifest.Response>(\"manifest.get\")",
            Exactly.Times(2));
        generated.Should().Contain("MethodName = \"manifest.get\"");
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static string RunGenerator(out Compilation compilationWithGenerated) {
        var handlersReference = CompileToImage(HandlersSource, "Sample.Handlers");
        return RunGenerator([handlersReference], out compilationWithGenerated);
    }

    private static string RunGenerator(IReadOnlyList<MetadataReference> handlerReferences, out Compilation compilationWithGenerated) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(HostSource, parseOptions);
        var references = CreateMetadataReferences().Concat(handlerReferences).ToArray();
        var hostCompilation = CSharpCompilation.Create(
            "Sample.Rpc",
            [hostTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Match the generated tree's parse options to the host tree so RunGeneratorsAndUpdateCompilation
        // produces a single-language-version compilation.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RpcMethodMapGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            hostCompilation, out var updated, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        compilationWithGenerated = updated;

        return driver.GetRunResult().GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), "RpcMethodMap.g.cs", StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static string ManifestOnlySource() {
        var rpc = EncodeFields(
            "manifest.get",
            "ManifestOnly",
            "global::ManifestOnly.GetManifest.Query",
            "global::ManifestOnly.GetManifest.Response",
            null,
            "1",
            "1",
            null,
            string.Empty);

        return $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.RpcMethod.v1", "{{rpc}}")]

            namespace ManifestOnly;

            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Name);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("manifest"));
            }
            """;
    }

    private static string EncodeFields(params string?[] fields) {
        var result = new System.Text.StringBuilder();
        foreach (var field in fields) {
            if (field is null) {
                result.Append("-1:");
                continue;
            }

            result.Append(field.Length);
            result.Append(':');
            result.Append(field);
        }

        return result.ToString();
    }

    private static ImmutableArray<Diagnostic> RunGeneratorDiagnostics(string handlersSource) {
        var handlersReference = CompileToImage(handlersSource, "Sample.Collide");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(HostSource, parseOptions);
        var references = CreateMetadataReferences().Append(handlersReference).ToArray();
        var hostCompilation = CSharpCompilation.Create(
            "Sample.Rpc.Collide", [hostTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RpcMethodMapGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGenerators(hostCompilation, TestContext.Current.CancellationToken);

        return driver.GetRunResult().Diagnostics;
    }

    private static MetadataReference CompileToImage(string source, string assemblyName) {
        var compilation = CompileWithManifest(source, assemblyName);

        using var stream = new MemoryStream();
        compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static MetadataReference CompileToCompilationReference(string source, string assemblyName) =>
        CompileWithManifest(source, assemblyName).ToMetadataReference();

    private static Compilation CompileWithManifest(string source, string assemblyName) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        Compilation compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out compilation, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return compilation;
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
