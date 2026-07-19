using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class IdempotencyGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Caching;
        using Elarion.Abstractions.Idempotency;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AttachesIdempotencyDecoratorAndPolicyToIdempotentCommand() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  [Idempotent]
                                  public sealed class PayHandler : IHandler<PayCommand, Result<PayResponse>> {
                                      public ValueTask<Result<PayResponse>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<PayResponse>.Success(new PayResponse("r")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_PayHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.IdempotencyDecorator<");
        generated.Should().Contain("GetRequiredService<global::Elarion.Abstractions.Pipeline.IUnitOfWork>()");
        generated.Should().Contain("GetRequiredService<global::Elarion.Abstractions.Idempotency.IIdempotencyStore>()");
        generated.Should().Contain("PayHandlerIdempotencyPolicy");
        // The AOT-safe non-generic envelope is (de)serialized through the framework context; the value goes
        // through options.GetTypeInfo(typeof(T)) — never a closed StoredResult<T> that no context registers.
        generated.Should().NotContain("global::Elarion.Abstractions.Idempotency.StoredResult<");
        generated.Should().Contain("ElarionFrameworkJsonContext.Default.StoredResult");
        generated.Should()
            .Contain("SerializeToElement(response.Value, options.GetTypeInfo(typeof(global::Sample.App.PayResponse)))");

        AssertCompiles(source, generated);
    }

    [Fact]
    public void AuthorizationWrapsIdempotency() {
        // Authorization must stay outermost so a denied request never claims a key.
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  [RequirePermission("billing", "write")]
                                  [Idempotent]
                                  public sealed class GuardedPayHandler : IHandler<PayCommand, Result<PayResponse>> {
                                      public ValueTask<Result<PayResponse>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<PayResponse>.Success(new PayResponse("r")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_GuardedPayHandler.g.cs");

        // Emission is innermost-first: idempotency is constructed before (inside) authorization.
        generated.IndexOf("IdempotencyDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("AuthorizationDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void DoesNotAttachToNonIdempotentHandler() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  public sealed class PlainHandler : IHandler<PayCommand, Result<PayResponse>> {
                                      public ValueTask<Result<PayResponse>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<PayResponse>.Success(new PayResponse("r")));
                                  }
                              }
                              """;

        var (result, _) = Run(source);
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void ReportsElidem001WhenResponseCannotRepresentFailure() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  [Idempotent]
                                  public sealed class BareHandler : IHandler<PayCommand, PayResponse> {
                                      public ValueTask<PayResponse> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(new PayResponse("r"));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELIDEM001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_BareHandler.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void ReportsElidem002OnNonCommand() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record ReadQuery(int Id) : IQuery;
                                  public sealed record ReadResponse(string Name);

                                  [Idempotent]
                                  public sealed class ReadHandler : IHandler<ReadQuery, Result<ReadResponse>> {
                                      public ValueTask<Result<ReadResponse>> HandleAsync(ReadQuery request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<ReadResponse>.Success(new ReadResponse("x")));
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELIDEM002" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
        GetGenerated(result, "Sample_App_ReadHandler.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void ReportsElidem004WhenAlsoCacheable() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  [Idempotent]
                                  [Cacheable("pay")]
                                  public sealed class BothHandler : IHandler<PayCommand, Result<PayResponse>> {
                                      public ValueTask<Result<PayResponse>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<PayResponse>.Success(new PayResponse("r")));
                                  }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELIDEM004" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record PayCommand(int Id) : ICommand;
                                  public sealed record PayResponse(string Receipt);

                                  [Idempotent]
                                  public sealed class PayHandler : IHandler<PayCommand, Result<PayResponse>> {
                                      public ValueTask<Result<PayResponse>> HandleAsync(PayCommand request, CancellationToken ct) =>
                                          ValueTask.FromResult(Result<PayResponse>.Success(new PayResponse("r")));
                                  }
                              }
                              """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "IdempotencyGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
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
            "IdempotencyGeneratorCompile",
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
