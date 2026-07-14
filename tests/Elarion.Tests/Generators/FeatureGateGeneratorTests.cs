using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class FeatureGateGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AttachesFeatureGateDecoratorToGatedHandler() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [FeatureGate("new-billing")]
                public sealed class GatedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_GatedHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.FeatureGateDecorator<");
        generated.Should().Contain("GetRequiredService<global::Elarion.Abstractions.Features.IFeatureFlagService>()");
        generated.Should().Contain("__handlerMetadata");
        // The feature gate is a functional gate just inside the observability decorator.
        generated.IndexOf("FeatureGateDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ObservabilityDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void AuthorizationWrapsFeatureGate() {
        // Authorization must stay the outermost functional gate, with the feature gate just inside it.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [RequirePermission("billing", "write")]
                [FeatureGate("new-billing")]
                public sealed class GuardedGatedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_GuardedGatedHandler.g.cs");

        // Emission is innermost-first, so the outer decorator is constructed later (higher index). Authorization
        // outermost => it appears after the feature gate in the generated factory.
        generated.IndexOf("FeatureGateDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("AuthorizationDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void DoesNotAttachToUngatedHandler() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record ReadThingQuery(int Id) : IQuery;
                public sealed record ReadThingResponse(string Name);

                public sealed class OpenHandler : IHandler<ReadThingQuery, Result<ReadThingResponse>> {
                    public ValueTask<Result<ReadThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        GetGenerated(result, "Sample_App_OpenHandler.g.cs").Should().NotContain("FeatureGateDecorator");
    }

    [Fact]
    public void ReportsElfeat001WhenResponseCannotRepresentFailure() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record PlainQuery(int Id) : IQuery;
                public sealed record PlainResponse(string Name);

                // Bare (non-Result) response: the gate cannot short-circuit -> ELFEAT001.
                [FeatureGate("x")]
                public sealed class PlainHandler : IHandler<PlainQuery, PlainResponse> {
                    public ValueTask<PlainResponse> HandleAsync(PlainQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(new PlainResponse("x"));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELFEAT001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("FeatureGateDecorator");
    }

    [Fact]
    public void ReportsElfeat002WhenFeatureNameMissing() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [FeatureGate]
                public sealed class EmptyGateHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (_, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELFEAT002" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void AttachesFeatureGateFromBaseHandlerClass() {
        // [FeatureGate] is Inherited = true, so a gate declared on a BASE handler must attach the decorator to the
        // derived handler — otherwise the derived handler ships ungated (C5).
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [FeatureGate("new-billing")]
                public abstract class GatedBase : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public abstract ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct);
                }

                public sealed class DerivedHandler : GatedBase {
                    public override ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_DerivedHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.FeatureGateDecorator<");
        AssertCompiles(source, generated);
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [FeatureGate("new-billing")]
                public sealed class GatedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "FeatureGateGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static void AssertCompiles(string source, string generated) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "FeatureGateGeneratorCompile",
            [CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(generated, parseOptions)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
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
