using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Declared upload limits (ADR-0100): a request's <c>[UploadLimits]</c> members get the upload-limits decorator and a
/// reflection-free declare function; a declaration no pipeline would enforce is a build error. Also the sort-map
/// ambiguity warning the schema export relies on.
/// </summary>
public sealed class UploadLimitsGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel.DataAnnotations;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string HandlerFile = "Sample_App_ImportStatement.g.cs";

    private const string Handler =
        """

        namespace Sample.App {
            [AppModule("App")]
            public static class AppModule { }

            [Handler]
            public sealed class ImportStatement : IHandler<ImportCommand, Result<int>> {
                public ValueTask<Result<int>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<int>>(1);
            }

        """;

    [Fact]
    public void AttachesTheDecorator_AndDeclaresEveryLimitedMember() {
        const string source = Preamble + Handler +
                              """
                                  public abstract record CommandBase {
                                      [UploadLimits(MaxBytes = 4096)]
                                      public UploadedFile? Signature { get; init; }
                                  }

                                  public sealed record ImportCommand(
                                      [property: UploadLimits(MaxBytes = 52428800, ContentTypes = ["text/csv", "application/xml"])]
                                      UploadedFile Statement,
                                      [UploadLimits(ContentTypes = ["application/pdf"])] UploadedFile[] Appendix) : CommandBase, ICommand {
                                      [MaxLength(10), UploadLimits(MaxBytes = 1024)]
                                      public IReadOnlyList<UploadedFile> Receipts { get; init; } = [];

                                      public UploadedFile? Unlimited { get; init; }
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);
        var generated = GetGenerated(result, HandlerFile);

        diagnostics.Should().BeEmpty();
        generated.Should().Contain(
            "private static readonly global::Elarion.Abstractions.UploadConstraints __uploadLimits0 = new() { ContentTypes = new string[] { \"application/pdf\" } };");
        generated.Should().Contain("__uploadLimits1 = new() { MaxBytes = 1024L };");
        generated.Should().Contain("__uploadLimits2 = new() { MaxBytes = 4096L };");
        generated.Should().Contain(
            "__uploadLimits3 = new() { MaxBytes = 52428800L, ContentTypes = new string[] { \"text/csv\", \"application/xml\" } };");
        generated.Should().Contain("""
                                           if (request.Appendix is { } __files0)
                                               foreach (var __file in __files0)
                                                   uploads.Declare(__file, __uploadLimits0);
                                   """);
        generated.Should().Contain("uploads.Declare(request.Signature, __uploadLimits2);");
        generated.Should().Contain("uploads.Declare(request.Statement, __uploadLimits3);");
        generated.Should().NotContain("request.Unlimited");
        generated.Should().Contain(
            "handler = new global::Elarion.Pipeline.UploadLimitsDecorator<global::Sample.App.ImportCommand, global::Elarion.Abstractions.Result<int>>(");
        generated.Should().Contain("sp.GetService<global::Elarion.Abstractions.IDeclaredUploadConstraints>(),");

        // Innermost: declared right before the handler runs, inside every other decorator.
        generated.IndexOf("UploadLimitsDecorator<global", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("new global::Elarion.Pipeline.ObservabilityDecorator<", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void LeavesARequestWithoutDeclaredLimitsAlone() {
        const string source = Preamble + Handler +
                              """
                                  public sealed record ImportCommand(UploadedFile Statement) : ICommand;
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Should().BeEmpty();
        GetGenerated(result, HandlerFile).Should().NotContain("UploadLimits");
    }

    [Theory]
    [InlineData("[UploadLimits(MaxBytes = 10)] public string? Name { get; init; }", "holds no UploadedFile")]
    [InlineData("[UploadLimits(MaxBytes = -1)] public UploadedFile? File { get; init; }", "MaxBytes is negative")]
    [InlineData("[UploadLimits] public UploadedFile? File { get; init; }", "declares no limit")]
    [InlineData("[UploadLimits(ContentTypes = [\" \"])] public UploadedFile? File { get; init; }", "blank content type")]
    public void ReportsAnInvalidDeclaration(string member, string reason) {
        var source = Preamble + Handler +
                     $$"""
                           public sealed record ImportCommand : ICommand {
                               {{member}}
                           }
                       }
                       """;

        var (result, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().Equal("ELUPL001");
        diagnostics[0].GetMessage().Should().Contain(reason);
        GetGenerated(result, HandlerFile).Should().NotContain("UploadLimitsDecorator");
    }

    [Fact]
    public void ReportsADeclarationOutsideAHandlerRequest() {
        const string source = Preamble + Handler +
                              """
                                  public sealed record ImportCommand(UploadedFile Statement) : ICommand;

                                  public sealed record Attachment([UploadLimits(MaxBytes = 10)] UploadedFile File);

                                  public sealed record Nested {
                                      [UploadLimits(MaxBytes = 10)]
                                      public UploadedFile? File { get; init; }
                                  }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().Equal("ELUPL002", "ELUPL002");
        diagnostics.Select(static d => d.GetMessage()).Should().Contain(m => m.Contains("'Sample.App.Attachment'"))
            .And.Contain(m => m.Contains("'Sample.App.Nested'"));
    }

    [Fact]
    public void ASingletonHandler_CannotDeclareUploadLimits() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record ImportCommand([property: UploadLimits(MaxBytes = 10)] UploadedFile Statement) : ICommand;

                                  [Handler(Scope = ServiceScope.Singleton)]
                                  public sealed class ImportStatement : IHandler<ImportCommand, Result<int>> {
                                      public ValueTask<Result<int>> HandleAsync(ImportCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult<Result<int>>(1);
                                  }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Should().Contain(d => d.Id == "ELSG013" && d.GetMessage().Contains("[UploadLimits]"));
    }

    [Fact]
    public void WarnsWhenAJsonRpcHandlerDeclaresMoreThanOneSortMap() {
        const string source = Preamble +
                              """

                              namespace Elarion.Paging {
                                  public sealed class SortMap<T> { }
                              }

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record Row(int Id);
                                  public sealed record ListQuery(string? Sort = null) : IQuery;

                                  [Handler]
                                  public sealed class ListRows : IHandler<ListQuery, Result<int>> {
                                      private static readonly Elarion.Paging.SortMap<Row> ByDate = new();
                                      private static readonly Elarion.Paging.SortMap<Row> ByName = new();

                                      public ValueTask<Result<int>> HandleAsync(ListQuery request, CancellationToken ct) =>
                                          ValueTask.FromResult<Result<int>>(1);
                                  }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().Equal("ELRPC012");
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostics[0].GetMessage().Should().Contain("declares 2 static SortMap fields");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "UploadLimitsGeneratorTests",
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
            "UploadLimitsGeneratorCompile",
            [CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(generated, parseOptions)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
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
