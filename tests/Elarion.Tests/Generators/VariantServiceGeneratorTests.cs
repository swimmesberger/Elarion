using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class VariantServiceGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string AlgorithmDefs =
        """

        namespace Sample.App {
            [AppModule("App")] public static class AppModule { }
            public interface IAlgorithm { }
            [Service]
            [FeatureVariant<IAlgorithm>("ForecastAlgorithm")]
            public sealed class LinearAlgo : IAlgorithm { }
            [Service]
            [FeatureVariant<IAlgorithm>("ForecastAlgorithm", Variant = "neural")]
            public sealed class NeuralAlgo : IAlgorithm { }
        }
        """;

    [Fact]
    public void HandlerInjectingVariantContract_IsWrappedInAsyncResolvedHandler() {
        var source = Preamble + AlgorithmDefs +
            """

            namespace Sample.App {
                public sealed record ForecastCommand(int Id) : ICommand;
                public sealed record ForecastResponse(string Name);

                public sealed class RunForecast(IAlgorithm algorithm)
                    : IHandler<ForecastCommand, Result<ForecastResponse>> {
                    public ValueTask<Result<ForecastResponse>> HandleAsync(ForecastCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ForecastResponse>.Success(new ForecastResponse("x")));
                }

                public sealed record PlainQuery(int Id) : IQuery;
                public sealed class PlainHandler : IHandler<PlainQuery, Result<ForecastResponse>> {
                    public ValueTask<Result<ForecastResponse>> HandleAsync(PlainQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ForecastResponse>.Success(new ForecastResponse("x")));
                }
            }
            """;

        var (result, _) = Run(new HandlerRegistrationGenerator(), source);

        var gated = GetGenerated(result, "Sample_App_RunForecast.g.cs");
        gated.Should().Contain("global::Elarion.Abstractions.Pipeline.AsyncResolvedHandler<");
        gated.Should().Contain("WarmAsync<global::Sample.App.IAlgorithm>");

        // A handler with no variant dependency keeps the synchronous registration.
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("AsyncResolvedHandler");
    }

    [Fact]
    public void EmitsKeyedRegistrations_Binding_And_TransparentFactory() {
        var (result, _) = Run(new VariantServiceRegistrationGenerator(), Preamble + AlgorithmDefs);

        var generated = AllGenerated(result);
        generated.Should().Contain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
        generated.Should().Contain("\"neural\"");
        generated.Should().Contain("global::Elarion.Abstractions.Features.VariantServiceKeys.Default");
        generated.Should().Contain("typeof(global::Sample.App.NeuralAlgo)");
    }

    [Fact]
    public void ReportsElvar002_WhenImplementationDoesNotImplementContract() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IAlgorithm { }
                [FeatureVariant<IAlgorithm>("ForecastAlgorithm")]
                public sealed class NotAnAlgorithm { }
            }
            """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR002" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar007_WhenVariantImplementationIsNotAService() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IAlgorithm { }
                [FeatureVariant<IAlgorithm>("ForecastAlgorithm")]
                public sealed class LinearAlgo : IAlgorithm { }
            }
            """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR007" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void ServiceGenerator_SkipsPlainRegistration_ForFeatureVariantClass() {
        // [FeatureVariant] is a modifier on [Service]: the service generator must NOT also emit a plain (unkeyed)
        // registration, or it would collide with the variant generator's transparent contract registration.
        var (result, _) = Run(new ModuleServiceRegistrationGenerator(), Preamble + AlgorithmDefs);

        var generated = AllGenerated(result);
        generated.Should().NotContain("LinearAlgoServiceRegistration");
        generated.Should().NotContain("NeuralAlgoServiceRegistration");
    }

    [Fact]
    public void ReportsElvar003_WhenNoDefaultImplementation() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IAlgorithm { }
                [Service]
                [FeatureVariant<IAlgorithm>("ForecastAlgorithm", Variant = "neural")]
                public sealed class NeuralAlgo : IAlgorithm { }
            }
            """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR003" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar001_WhenDuplicateVariantKey() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IAlgorithm { }
                [Service]
                [FeatureVariant<IAlgorithm>("ForecastAlgorithm", Variant = "neural")]
                public sealed class NeuralA : IAlgorithm { }
                [Service]
                [FeatureVariant<IAlgorithm>("ForecastAlgorithm", Variant = "neural")]
                public sealed class NeuralB : IAlgorithm { }
            }
            """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void IrrelevantEditReusesVariantPipeline() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new VariantServiceRegistrationGenerator(), Preamble + AlgorithmDefs, "VariantServices");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(
        IIncrementalGenerator generator, string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "VariantServiceGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        var result = driver.GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
