using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// The <see cref="HandlerRegistrationGenerator"/> half of declarative request validation (ADR-0027): the
/// framework <c>ValidationDecorator</c> auto-attaches for any handler whose request type graph carries
/// DataAnnotations validation metadata, positioned just inside the feature gate
/// (tracing → authorization → feature gate → validation → [DefaultPipeline] list → handler).
/// </summary>
public sealed class ValidationDecoratorGeneratorTests {
    private const string Preamble =
        """
        using System.ComponentModel.DataAnnotations;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AttachesValidationDecoratorJustInsideFeatureGate() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record CreateThingCommand : ICommand {
                                      [StringLength(100, MinimumLength = 3)]
                                      public required string Name { get; init; }
                                  }

                                  public sealed record CreateThingResponse(string Name);

                                  [RequirePermission("things", "write")]
                                  [FeatureGate("new-things")]
                                  public sealed class CreateThingHandler : IHandler<CreateThingCommand, Result<CreateThingResponse>> {
                                      public ValueTask<Result<CreateThingResponse>> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<CreateThingResponse>.Success(new CreateThingResponse("x")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_CreateThingHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.ValidationDecorator<");
        generated.Should().Contain("GetRequiredService<global::Elarion.Abstractions.Validation.IRequestValidator>()");
        // Emission is innermost-first, so an outer decorator is constructed later (higher index). The contract:
        // observability → authorization → feature gate → validation → handler.
        generated.IndexOf("ValidationDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("FeatureGateDecorator", StringComparison.Ordinal));
        generated.IndexOf("FeatureGateDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("AuthorizationDecorator", StringComparison.Ordinal));
        generated.IndexOf("AuthorizationDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ObservabilityDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void ValidationWrapsDefaultPipelineDecorators() {
        // The [DefaultPipeline]-style [DecoratorList] chain (e.g. the transaction) runs INSIDE validation: an
        // invalid request must fail before it can open a transaction or touch the pipeline.
        const string source = Preamble +
                              """

                              [assembly: Sample.App.AppPipeline]

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed class FakeTransactionDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                                      : IHandler<TRequest, TResponse> {
                                      public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                                          inner.HandleAsync(request, ct);
                                  }

                                  [Elarion.Abstractions.Pipeline.DecoratorList(typeof(FakeTransactionDecorator<,>))]
                                  [System.AttributeUsage(System.AttributeTargets.Assembly | System.AttributeTargets.Class)]
                                  public sealed class AppPipelineAttribute : System.Attribute { }

                                  public sealed record CreateThingCommand : ICommand {
                                      [StringLength(100)]
                                      public required string Name { get; init; }
                                  }

                                  public sealed record CreateThingResponse(string Name);

                                  public sealed class CreateThingHandler : IHandler<CreateThingCommand, Result<CreateThingResponse>> {
                                      public ValueTask<Result<CreateThingResponse>> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<CreateThingResponse>.Success(new CreateThingResponse("x")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_CreateThingHandler.g.cs");

        generated.Should().Contain("FakeTransactionDecorator<");
        generated.IndexOf("FakeTransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ValidationDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void AttachesWhenOnlyNestedTypeIsAnnotated() {
        // The request itself is clean; a transitively reachable property type carries the constraint — the
        // shared graph walk must still mark the request validatable.
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record CustomerAddress {
                                      [StringLength(64)]
                                      public required string Street { get; init; }
                                  }

                                  public sealed record RegisterCustomerCommand : ICommand {
                                      public required CustomerAddress Address { get; init; }
                                  }

                                  public sealed record RegisterCustomerResponse(string Id);

                                  public sealed class RegisterCustomerHandler
                                      : IHandler<RegisterCustomerCommand, Result<RegisterCustomerResponse>> {
                                      public ValueTask<Result<RegisterCustomerResponse>> HandleAsync(RegisterCustomerCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<RegisterCustomerResponse>.Success(new RegisterCustomerResponse("x")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_RegisterCustomerHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.ValidationDecorator<");
    }

    [Fact]
    public void DoesNotAttachToUnannotatedHandler() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record ReadThingQuery(int Id) : IQuery;
                                  public sealed record ReadThingResponse(string Name);

                                  public sealed class ReadThingHandler : IHandler<ReadThingQuery, Result<ReadThingResponse>> {
                                      public ValueTask<Result<ReadThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);

        GetGenerated(result, "Sample_App_ReadThingHandler.g.cs").Should().NotContain("ValidationDecorator");
    }

    [Fact]
    public void ReportsElval001WhenResponseCannotRepresentFailure() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record CreateThingCommand : ICommand {
                                      [StringLength(100)]
                                      public required string Name { get; init; }
                                  }

                                  public sealed record PlainResponse(string Name);

                                  // Bare (non-Result) response: the validation check cannot short-circuit -> ELVAL001.
                                  public sealed class PlainHandler : IHandler<CreateThingCommand, PlainResponse> {
                                      public ValueTask<PlainResponse> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(new PlainResponse("x"));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELVAL001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("ValidationDecorator");
    }

    [Fact]
    public void DoesNotAttachWithoutElarionValidationReference() {
        // Attach is conditional on the enforcement package: without the Elarion.Validation reference no
        // decorator attaches (the ValidationResolverGenerator's ELVAL002 makes the gap visible).
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record CreateThingCommand : ICommand {
                                      [StringLength(100)]
                                      public required string Name { get; init; }
                                  }

                                  public sealed record CreateThingResponse(string Name);

                                  public sealed class CreateThingHandler : IHandler<CreateThingCommand, Result<CreateThingResponse>> {
                                      public ValueTask<Result<CreateThingResponse>> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<CreateThingResponse>.Success(new CreateThingResponse("x")));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source, true);

        GetGenerated(result, "Sample_App_CreateThingHandler.g.cs").Should().NotContain("ValidationDecorator");
        diagnostics.Should().NotContain(d => d.Id == "ELVAL001");
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record CreateThingCommand : ICommand {
                                      [StringLength(100, MinimumLength = 3)]
                                      public required string Name { get; init; }
                                  }

                                  public sealed record CreateThingResponse(string Name);

                                  public sealed class CreateThingHandler : IHandler<CreateThingCommand, Result<CreateThingResponse>> {
                                      public ValueTask<Result<CreateThingResponse>> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<CreateThingResponse>.Success(new CreateThingResponse("x")));
                                  }
                              }
                              """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(
        string source,
        bool excludeElarionValidationReference = false) {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "ValidationDecoratorGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(excludeElarionValidationReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static void AssertCompiles(string source, string generated) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ValidationDecoratorGeneratorCompile",
            [
                CSharpSyntaxTree.ParseText(source, parseOptions,
                    cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(generated, parseOptions,
                    cancellationToken: TestContext.Current.CancellationToken)
            ],
            CreateMetadataReferences(false),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences(bool excludeElarionValidation) {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Where(path => !excludeElarionValidation ||
                           !string.Equals(Path.GetFileName(path), "Elarion.Validation.dll",
                               StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
