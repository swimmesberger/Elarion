using System.Reflection;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Authorization;
using Elarion.Generators;
using Elarion.Pipeline;
using Elarion.Tests.Authorization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class VisibilityGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;
        using Elarion.Abstractions.Paging;
        using Microsoft.Extensions.DependencyInjection;

        [assembly: UseElarion]
        """;

    private const string PreviewSample = Preamble +
                                         """

                                         namespace Sample.App {
                                             [AppModule("App")]
                                             public static class AppModule { }

                                             public sealed record Booking(Guid Id, decimal Amount);

                                             public sealed record Line {
                                                 public required int Row { get; init; }
                                                 [VisibleWith("banking.read")] public Booking? Resembles { get; init; }
                                             }

                                             public readonly record struct Total(decimal Net, [property: VisibleWith("banking", "read")] decimal? Opening);

                                             public sealed record Preview(
                                                 IReadOnlyList<Line> Lines,
                                                 Line[] Held,
                                                 List<Line> Skipped,
                                                 ImmutableArray<Line> Matched,
                                                 Total Total,
                                                 Total? Previous,
                                                 [VisibleWith("banking", "read")] string? Account,
                                                 string Note,
                                                 Page<Line> Page);

                                             public sealed record PreviewImport : ICommand;

                                             [RequirePermission("banking", "write")]
                                             public sealed class PreviewImportHandler : IHandler<PreviewImport, Result<Preview>> {
                                                 public static Preview Last = null!;

                                                 public ValueTask<Result<Preview>> HandleAsync(PreviewImport request, CancellationToken ct) {
                                                     var line = new Line { Row = 1, Resembles = new Booking(Guid.Empty, 12m) };
                                                     Last = new Preview([line], [line], [line], [line], new Total(10m, 5m), new Total(1m, 2m), "AT61", "kept", new Page<Line> { Items = [line], Total = 1 });
                                                     return ValueTask.FromResult(Result<Preview>.Success(Last));
                                                 }
                                             }

                                             public static class Probe {
                                                 public static async Task<string> Run(IServiceProvider services) {
                                                     using var scope = services.CreateScope();
                                                     var handler = scope.ServiceProvider.GetRequiredService<IHandler<PreviewImport, Result<Preview>>>();
                                                     var result = await handler.HandleAsync(new PreviewImport(), CancellationToken.None);
                                                     var p = result.Value;
                                                     return string.Join("|",
                                                         ReferenceEquals(p, PreviewImportHandler.Last),
                                                         p.Lines[0].Resembles is null, p.Held[0].Resembles is null, p.Skipped[0].Resembles is null,
                                                         p.Matched[0].Resembles is null, p.Total.Opening is null, p.Previous!.Value.Opening is null,
                                                         p.Account is null, p.Page.Items[0].Resembles is null, p.Page.Total, p.Note,
                                                         p.Lines[0].Row, p.Total.Net);
                                                 }
                                             }
                                         }
                                         """;

    [Fact]
    public void HidesMarkedMembersFromACallerWithoutThePermission() {
        var output = RunProbe(PreviewSample, "banking.write");

        // A copy, every marked member hidden at every depth, every other member kept.
        output.Should().Be("False|True|True|True|True|True|True|True|True|1|kept|1|10");
    }

    [Fact]
    public void AnswersTheHandlersValueToACallerWithThePermission() {
        var output = RunProbe(PreviewSample, "banking.write", "banking.read");

        output.Should().Be("True|False|False|False|False|False|False|False|False|1|kept|1|10");
    }

    [Fact]
    public void AttachesTheDecoratorInsideAuthorization() {
        var generated = GetGenerated(Run(PreviewSample).Result, "Sample_App_PreviewImportHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.VisibilityDecorator<global::Sample.App.PreviewImport, global::Sample.App.Preview>(");
        generated.Should().Contain("private static readonly string[] __visiblePermissions = new string[] { \"banking.read\" };");
        // Emitted innermost first: the visibility decorator is wrapped by authorization, and wraps the feature gate.
        generated.IndexOf("VisibilityDecorator<", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("AuthorizationDecorator<", StringComparison.Ordinal));
    }

    [Fact]
    public void LeavesAResponseWithoutMarkedMembersUntouched() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record Row(int Id, string? Name);
                                  public sealed record ListRows : IQuery;

                                  public sealed class ListRowsHandler : IHandler<ListRows, Result<IReadOnlyList<Row>>> {
                                      public ValueTask<Result<IReadOnlyList<Row>>> HandleAsync(ListRows request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<IReadOnlyList<Row>>.Success([]));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        GetGenerated(result, "Sample_App_ListRowsHandler.g.cs").Should().NotContain("Visibility")
            .And.NotContain("__Hide");
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void ReportsANonNullableMarkedMember() {
        var diagnostics = DiagnosticsFor(
            """
            public sealed record Row(int Id, [property: VisibleWith("banking.read")] decimal Balance);
            """,
            "Row");

        diagnostics.Should().ContainSingle(d => d.Id == "ELAUTH003")
            .Which.GetMessage().Should().Contain("'Balance'");
    }

    [Theory]
    [InlineData("public sealed class Row { public int Id { get; init; } [VisibleWith(\"banking.read\")] public string? Iban { get; init; } }",
        "Row", "neither a record nor a struct")]
    [InlineData("public sealed record Row { [VisibleWith(\"banking.read\")] public string? Iban { get; } }",
        "Row", "has no accessible init or set accessor")]
    [InlineData("public sealed record Row { [VisibleWith(\"banking.read\")] public string? Iban { get; init; } }",
        "HashSet<Row>", "a collection type the pipeline cannot copy")]
    [InlineData("public sealed record Row { [VisibleWith(\"banking.read\")] public string? Iban { get; init; } }",
        "Dictionary<string, Row>", "a collection type the pipeline cannot copy")]
    public void ReportsAShapeThePipelineCannotCopy(string declaration, string valueType, string reason) {
        var diagnostics = DiagnosticsFor(declaration, valueType);

        diagnostics.Should().ContainSingle(d => d.Id == "ELAUTH004")
            .Which.GetMessage().Should().Contain(reason);
    }

    [Fact]
    public void FollowsAMarkedMemberThroughACycle() {
        // A tree whose children are only reached through the cycle still has its marked member hidden in them.
        const string tree =
            """
            public sealed record Node {
                public IReadOnlyList<Node> Children { get; init; } = [];
                public Leaf? Leaf { get; init; }
            }
            public sealed record Leaf { [VisibleWith("banking.read")] public string? Iban { get; init; } }
            """;

        var (result, diagnostics) = Run(HandlerSource(tree, "Node"));

        diagnostics.Should().BeEmpty();
        var generated = GetGenerated(result, "Sample_App_ShowHandler.g.cs");
        generated.Should().Contain("Children = __Hide1(value.Children, permissions)");
        generated.Should().Contain("ResponseRedaction.Sequence(value, __Hide0, permissions)");
    }

    [Fact]
    public void APreviewOfAnOutcomeWithMarkedMembersIsFingerprintedAsTheCallerSeesIt() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  using Elarion.Abstractions.Previews;

                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record Outcome : IPreviewOutcome<Outcome> {
                                      public int Imported { get; init; }
                                      [VisibleWith("banking.read")] public decimal? Opening { get; init; }
                                      public string? PreviewFingerprint { get; init; }
                                      public Outcome WithPreviewFingerprint(string? fingerprint) => this with { PreviewFingerprint = fingerprint };
                                  }

                                  public sealed record Import(bool Preview = false, string? PreviewFingerprint = null) : IPreviewableRequest;

                                  public sealed class ImportHandler : IHandler<Import, Result<Outcome>> {
                                      public ValueTask<Result<Outcome>> HandleAsync(Import request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<Outcome>.Success(new Outcome()));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var generated = GetGenerated(result, "Sample_App_ImportHandler.g.cs");
        generated.Should().Contain(
            "global::Elarion.Pipeline.PreviewDecorator<global::Sample.App.Import, global::Sample.App.Outcome>(\n"
            + "                    handler,\n"
            + "                    sp.GetService<global::Elarion.Abstractions.Pipeline.IUnitOfWork>(),\n"
            + "                    sp.GetRequiredService<global::Elarion.Abstractions.Serialization.IElarionJsonSerialization>(),\n"
            + "                    sp.GetService<global::Elarion.Abstractions.Authorization.IPermissionChecker>(),\n"
            + "                    __Hide0);");
        Compile(source);
    }

    [Fact]
    public void ReusesOutputsAfterAnIrrelevantEdit() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(new HandlerRegistrationGenerator(), PreviewSample,
            "Handlers");
    }

    [Fact]
    public async Task TheDecoratorHidesThePayloadOfAFailureThatCarriesTheResponse() {
        var stale = new Holder("secret");
        var inner = new StubInnerHandler<string, Result<Holder>>(
            AppError.Conflict("Changed.", "preview.changed", stale));
        var decorator = new VisibilityDecorator<string, Holder>(inner, null, ["banking.read"],
            static (value, permissions) => value with { Secret = permissions.Holds("banking.read") ? value.Secret : null });

        var result = await decorator.HandleAsync("x", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("preview.changed");
        result.Error.Data.Should().Be(new Holder(null));
    }

    [Fact]
    public async Task WithoutACheckerEveryMarkedMemberIsHidden() {
        var inner = new StubInnerHandler<string, Result<Holder>>(new Holder("secret"));
        var decorator = new VisibilityDecorator<string, Holder>(inner, null, ["banking.read"],
            static (value, permissions) => value with { Secret = permissions.Holds("banking.read") ? value.Secret : null });

        var result = await decorator.HandleAsync("x", TestContext.Current.CancellationToken);

        result.Value.Secret.Should().BeNull();
    }

    private sealed record Holder(string? Secret);

    private static IReadOnlyList<Diagnostic> DiagnosticsFor(string declarations, string valueType) {
        return Run(HandlerSource(declarations, valueType)).Diagnostics;
    }

    private static string HandlerSource(string declarations, string valueType) {
        return Preamble +
               $$"""

                 namespace Sample.App {
                     [AppModule("App")]
                     public static class AppModule { }

                     {{declarations}}

                     public sealed record Show : IQuery;

                     public sealed class ShowHandler : IHandler<Show, Result<{{valueType}}>> {
                         public ValueTask<Result<{{valueType}}>> HandleAsync(Show request, CancellationToken ct) =>
                             ValueTask.FromResult(Result<{{valueType}}>.Success(default!));
                     }
                 }
                 """;
    }

    private static string RunProbe(string source, params string[] permissions) {
        var assembly = Compile(source);
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser {
            IsAuthenticated = true, Claims = [.. permissions.Select(static p => ("permission", p))]
        });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddElarionAuthorization();
        assembly.GetType("Sample.App.PreviewImportHandlerRegistration")!
            .GetMethod("AddPreviewImportHandler", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [services]);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var run = (Task<string>)assembly.GetType("Sample.App.Probe")!.GetMethod("Run")!.Invoke(null, [provider])!;
        return run.GetAwaiter().GetResult();
    }

    private static Assembly Compile(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "VisibilitySample" + Guid.CreateVersion7().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: TestContext.Current.CancellationToken)],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new HandlerRegistrationGenerator().AsSourceGenerator(), new ModuleDefaultServicesGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics,
            TestContext.Current.CancellationToken);
        generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        using var image = new MemoryStream();
        output.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        return Assembly.Load(image.ToArray());
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "VisibilityGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new HandlerRegistrationGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        var result = driver.RunGenerators(compilation).GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<MetadataReference> References() {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => SharedMetadataReferences.FromFile(path));
        return tpa.Concat([
            SharedMetadataReferences.FromFile(typeof(IHandler<,>).Assembly.Location),
            SharedMetadataReferences.FromFile(typeof(VisibilityDecorator<,>).Assembly.Location),
            SharedMetadataReferences.FromFile(typeof(IPermissionChecker).Assembly.Location),
            SharedMetadataReferences.FromFile(typeof(ClaimsPermissionChecker).Assembly.Location)
        ]).Distinct().ToArray();
    }
}
