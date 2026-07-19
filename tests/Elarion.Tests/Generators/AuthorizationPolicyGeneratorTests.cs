using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class AuthorizationPolicyGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AutoRegistersPolicyPerModuleAndComposes() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }

                                  [AuthorizationPolicy("AtLeast21")]
                                  public sealed class AtLeast21Policy : IAuthorizationPolicy {
                                      public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) =>
                                          ValueTask.FromResult(true);
                                  }
                              }
                              """;

        var result = Generate(source);
        var extensions = GetGenerated(result, "AppAuthorizationPolicyExtensions.g.cs");

        extensions.Should().Contain("public static IServiceCollection AddAppAuthorizationPolicies(");
        extensions.Should().Contain(
            "services.AddElarionAuthorizationPolicy<global::Sample.App.AtLeast21Policy>(\"AtLeast21\");");
        // The ConfigureDefaultServices filler wires it into the module aggregation (the 7th category hook).
        result.GeneratedTrees
            .Select(tree => tree.GetText().ToString())
            .Should()
            .Contain(text =>
                text.Contains("static partial void AddAuthorizationPolicies", StringComparison.Ordinal) &&
                text.Contains("AddAppAuthorizationPolicies(services)", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsElpol001WhenNotAnAuthorizationPolicy() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }

                                  [AuthorizationPolicy("Broken")]
                                  public sealed class NotAPolicy { }
                              }
                              """;

        var diagnostics = RunForDiagnostics(source);

        diagnostics.Any(d => d.Id == "ELPOL001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void ReportsElpol002WhenPolicyNotInAnyModule() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }
                              }

                              namespace Sample.Outside {
                                  [AuthorizationPolicy("Orphan")]
                                  public sealed class OrphanPolicy : IAuthorizationPolicy {
                                      public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) =>
                                          ValueTask.FromResult(true);
                                  }
                              }
                              """;

        var diagnostics = RunForDiagnostics(source);

        diagnostics.Any(d => d.Id == "ELPOL002" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void IrrelevantEditReusesPolicies() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static partial class AppModule { }

                                  [AuthorizationPolicy("AtLeast21")]
                                  public sealed class AtLeast21Policy : IAuthorizationPolicy {
                                      public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) =>
                                          ValueTask.FromResult(true);
                                  }
                              }
                              """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new AuthorizationPolicyRegistrationGenerator(), source, "AuthorizationPolicies",
            "AuthorizationPoliciesCombined");
    }

    private static GeneratorDriverRunResult Generate(string source) {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "AuthorizationPolicyGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The ConfigureDefaultServices skeleton (ModuleDefaultServicesGenerator) declares the partial hooks this
        // generator's filler implements, so both run together.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] {
                new AuthorizationPolicyRegistrationGenerator().AsSourceGenerator(),
                new ModuleDefaultServicesGenerator().AsSourceGenerator()
            },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> RunForDiagnostics(string source) {
        var ct = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create(
            "AuthorizationPolicyGeneratorDiagnostics",
            [
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
                    cancellationToken: ct)
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AuthorizationPolicyRegistrationGenerator());
        return driver.RunGenerators(compilation, ct).GetRunResult().Diagnostics;
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
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
