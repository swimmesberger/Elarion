using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class PreviewGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Idempotency;
        using Elarion.Abstractions.Modules;
        using Elarion.Abstractions.Pipeline;
        using Elarion.Abstractions.Previews;

        [assembly: UseElarion]
        """;

    private const string Outcome =
        """
            public sealed record ImportOutcome(int NewRows, int HeldBack) : IPreviewOutcome<ImportOutcome> {
                public string? PreviewFingerprint { get; init; }
                public ImportOutcome WithPreviewFingerprint(string? fingerprint) => this with { PreviewFingerprint = fingerprint };
            }

            public sealed record ImportCommand(int StatementId, bool Preview = false, string? PreviewFingerprint = null)
                : IPreviewableRequest;
        """;

    [Fact]
    public void AttachesThePreviewDecoratorToAPreviewableCommand() {
        const string source = Preamble +
                              $$"""

                                namespace Sample.App {
                                    [AppModule("App")]
                                    public static class AppModule { }

                                {{Outcome}}

                                    public sealed class ImportHandler : IHandler<ImportCommand, Result<ImportOutcome>> {
                                        public ValueTask<Result<ImportOutcome>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                            ValueTask.FromResult<Result<ImportOutcome>>(new ImportOutcome(1, 0));
                                    }
                                }
                                """;

        var (result, diagnostics) = Run(source);
        var generated = GetGenerated(result, "Sample_App_ImportHandler.g.cs");

        diagnostics.Should().BeEmpty();
        generated.Should().Contain(
            "handler = new global::Elarion.Pipeline.PreviewDecorator<global::Sample.App.ImportCommand, global::Sample.App.ImportOutcome>(");
        // Soft: an ordinary call still works on a host without a unit of work; a preview there fails closed.
        generated.Should().Contain("sp.GetService<global::Elarion.Abstractions.Pipeline.IUnitOfWork>()");
        generated.Should().Contain("typeof(global::Elarion.Pipeline.PreviewDecorator<,>), false");

        AssertCompiles(source, generated);
    }

    [Fact]
    public void PreviewDecorator_SitsInsideIdempotency_AndOutsideTheDecoratorList() {
        const string source = Preamble +
                              $$"""

                                namespace Sample.App {
                                    [DecoratorList(typeof(Elarion.Pipeline.TransactionDecorator<,>))]
                                    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                                    public sealed class DefaultPipelineAttribute : Attribute;

                                    [AppModule("App")]
                                    [DefaultPipeline]
                                    public static class AppModule { }

                                {{Outcome}}

                                    [Idempotent]
                                    public sealed class ImportHandler : IHandler<ImportCommand, Result<ImportOutcome>> {
                                        public ValueTask<Result<ImportOutcome>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                            ValueTask.FromResult<Result<ImportOutcome>>(new ImportOutcome(1, 0));
                                    }
                                }
                                """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_ImportHandler.g.cs");

        // Emission is innermost-first: the [DecoratorList] entry, then the preview decorator, then idempotency.
        var transaction = generated.IndexOf("new global::Elarion.Pipeline.TransactionDecorator<", StringComparison.Ordinal);
        var preview = generated.IndexOf("new global::Elarion.Pipeline.PreviewDecorator<", StringComparison.Ordinal);
        var idempotency = generated.IndexOf("new global::Elarion.Pipeline.IdempotencyDecorator<", StringComparison.Ordinal);
        transaction.Should().BePositive();
        preview.Should().BeGreaterThan(transaction);
        idempotency.Should().BeGreaterThan(preview);

        AssertCompiles(source, generated);
    }

    [Fact]
    public void LeavesOrdinaryCommandsAlone() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;

                                  public sealed class PayHandler : IHandler<PayCommand, Result<string>> {
                                      public ValueTask<Result<string>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult<Result<string>>("ok");
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Should().BeEmpty();
        GetGenerated(result, "Sample_App_PayHandler.g.cs").Should().NotContain("PreviewDecorator");
    }

    [Theory]
    [InlineData("Result<string>", "ValueTask.FromResult<Result<string>>(\"x\")")]
    [InlineData("string", "ValueTask.FromResult(\"x\")")]
    public void ReportsElprev001_WhenTheResponseIsNotAPreviewOutcome(string response, string body) {
        var source = Preamble +
                     $$"""

                       namespace Sample.App {
                           [AppModule("App")]
                           public static class AppModule { }

                       {{Outcome}}

                           public sealed class ImportHandler : IHandler<ImportCommand, {{response}}> {
                               public ValueTask<{{response}}> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                   {{body}};
                           }
                       }
                       """;

        var (result, diagnostics) = Run(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELPREV001" && d.Severity == DiagnosticSeverity.Error)
            .Which.GetMessage().Should().Contain("Sample.App.ImportHandler").And.Contain("IPreviewOutcome");
        GetGenerated(result, "Sample_App_ImportHandler.g.cs").Should().NotContain("PreviewDecorator");
    }

    [Fact]
    public void ReportsElsg013_ForASingletonPreviewableHandler() {
        const string source = Preamble +
                              $$"""

                                namespace Sample.App {
                                    [AppModule("App")]
                                    public static class AppModule { }

                                {{Outcome}}

                                    [Handler(Scope = ServiceScope.Singleton)]
                                    public sealed class ImportHandler : IHandler<ImportCommand, Result<ImportOutcome>> {
                                        public ValueTask<Result<ImportOutcome>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                            ValueTask.FromResult<Result<ImportOutcome>>(new ImportOutcome(1, 0));
                                    }
                                }
                                """;

        var (_, diagnostics) = Run(source);

        diagnostics.Should().Contain(d => d.Id == "ELSG013" && d.GetMessage().Contains("preview decorator"));
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        const string source = Preamble +
                              $$"""

                                namespace Sample.App {
                                    [AppModule("App")]
                                    public static class AppModule { }

                                {{Outcome}}

                                    public sealed class ImportHandler : IHandler<ImportCommand, Result<ImportOutcome>> {
                                        public ValueTask<Result<ImportOutcome>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                            ValueTask.FromResult<Result<ImportOutcome>>(new ImportOutcome(1, 0));
                                    }
                                }
                                """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "PreviewGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
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
            "PreviewGeneratorCompile",
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
            .Select(path => SharedMetadataReferences.FromFile(path))
            .ToArray();
    }
}
