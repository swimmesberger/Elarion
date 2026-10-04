using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class FeatureFlagGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        """;

    private const string UseElarion = "[assembly: UseElarion]\n";

    private const string BillingModule =
        """
        namespace Sample.Billing {
            [AppModule("Billing")]
            public static class BillingModule { }

            [FeatureFlag("beta-reports", Description = "Early access to reports.", ExposeToClient = true)]
            public sealed class BetaReportsFlag : IFeatureFlagResolver {
                public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) =>
                    ValueTask.FromResult(context.UserId is not null);
            }

            [BackendFeatureFlag("new-export", Description = "The streaming export.")]
            [BackendFeatureFlag("dashboard-v2", ExposeToClient = true)]
            public static class BillingFlags { }
        }
        """;

    [Fact]
    public void RegistersCodeAndBackendFlagsIntoTheDeclaringModulesDefaultServices() {
        var run = Run(Preamble + BillingModule);

        run.Errors.Should().BeEmpty();
        var filler = Generated(run, "Sample_Billing_BillingModuleElarionModuleServices.FeatureFlags.g.cs");
        filler.Should().Contain("static partial void AddFeatureFlags(");
        filler.Should().Contain(
            "AddElarionBackendFeatureFlag(services, \"dashboard-v2\", \"Billing\", null, true);");
        filler.Should().Contain(
            "AddElarionBackendFeatureFlag(services, \"new-export\", \"Billing\", \"The streaming export.\", false);");
        filler.Should().Contain(
            "AddElarionFeatureFlag<global::Sample.Billing.BetaReportsFlag>(services, \"beta-reports\", \"Billing\", \"Early access to reports.\", true);");
    }

    [Fact]
    public void FlagUsedByGateOrVariantButDeclaredNowhere_ReportsELFLAG001() {
        var run = Run(Preamble + BillingModule +
                      """

                      namespace Sample.Billing {
                          public sealed record Cmd : ICommand;
                          [FeatureGate("new-export", "typo-flag")]
                          public sealed class GatedHandler { }

                          public interface IAlgo { }
                          [Service]
                          [FeatureVariant("missing-algo-flag", Variant = "x")]
                          public sealed class XAlgo : IAlgo { }
                      }
                      """);

        var undeclared = run.Diagnostics.Where(d => d.Id == "ELFLAG001").ToList();
        undeclared.Should().HaveCount(2);
        undeclared.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("'typo-flag'") && m.Contains("FeatureGate"));
        undeclared.Select(d => d.GetMessage()).Should()
            .Contain(m => m.Contains("'missing-algo-flag'") && m.Contains("FeatureVariant"));
        // The declared flag in the same gate is not reported.
        undeclared.Select(d => d.GetMessage()).Should().NotContain(m => m.Contains("'new-export'"));
        undeclared.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error && d.Location.Kind != LocationKind.None);
    }

    [Fact]
    public void FlagDeclaredInAReferencedAssembly_SatisfiesAGateInTheReferencingAssembly() {
        var reference = CompileToImage(Preamble + BillingModule, "Sample.Billing.Lib");

        var run = Run(
            Preamble +
            """
            namespace Sample.Host {
                [AppModule("Host")]
                public static class HostModule { }

                [FeatureGate("new-export")]
                public sealed class GatedHandler { }
            }
            """,
            [reference]);

        run.Diagnostics.Where(d => d.Id.StartsWith("ELFLAG", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public void FlagDeclaredTwiceInOneAssembly_ReportsELFLAG002AndRegistersNeither() {
        var run = Run(Preamble + BillingModule +
                      """

                      namespace Sample.Billing {
                          [BackendFeatureFlag("beta-reports")]
                          public static class MoreFlags { }
                      }
                      """);

        var duplicates = run.Diagnostics.Where(d => d.Id == "ELFLAG002").ToList();
        duplicates.Should().HaveCount(2);
        duplicates.Should().OnlyContain(d => d.GetMessage().Contains("'beta-reports'"));
        run.Result.GeneratedTrees.Select(t => t.GetText().ToString())
            .Should().NotContain(text => text.Contains("\"beta-reports\"") && text.Contains("AddElarion"));
    }

    [Fact]
    public void FlagDeclaredInAReferencedAssemblyAndHere_ReportsELFLAG002AtTheLocalDeclaration() {
        var reference = CompileToImage(Preamble + BillingModule, "Sample.Billing.Lib");

        var run = Run(
            Preamble +
            """
            namespace Sample.Host {
                [AppModule("Host")]
                public static class HostModule { }

                [BackendFeatureFlag("new-export")]
                public static class HostFlags { }
            }
            """,
            [reference]);

        var duplicate = run.Diagnostics.Should().ContainSingle(d => d.Id == "ELFLAG002").Subject;
        duplicate.Location.Kind.Should().NotBe(LocationKind.None);
        duplicate.GetMessage().Should().Contain("referenced assembly").And.Contain("Sample.Host.HostFlags");
    }

    [Fact]
    public void FlagDeclaredByTwoReferencedAssemblies_ReportsELFLAG002OnceWithoutALocation() {
        var first = CompileToImage(Preamble + BillingModule, "Sample.Billing.One");
        var second = CompileToImage(Preamble + BillingModule.Replace("Sample.Billing", "Sample.Billing2")
            .Replace("\"Billing\"", "\"Billing2\""), "Sample.Billing.Two");

        var run = Run(Preamble + "namespace Sample.Host { [AppModule(\"Host\")] public static class HostModule { } }",
            [first, second]);

        run.Diagnostics.Where(d => d.Id == "ELFLAG002").Should().NotBeEmpty()
            .And.OnlyContain(d => d.Location.Kind == LocationKind.None);
    }

    [Fact]
    public void FeatureFlagOnAClassThatIsNotAResolver_ReportsELFLAG003AndRegistersNothing() {
        var run = Run(Preamble +
                      """
                      namespace Sample.Billing {
                          [AppModule("Billing")]
                          public static class BillingModule { }

                          [FeatureFlag("orphan")]
                          public sealed class NotAResolver { }

                          [FeatureFlag("abstract-flag")]
                          public abstract class AbstractResolver : IFeatureFlagResolver {
                              public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) => default;
                          }
                      }
                      """);

        var diagnostics = run.Diagnostics.Where(d => d.Id == "ELFLAG003").ToList();
        diagnostics.Should().HaveCount(2);
        diagnostics.Select(d => d.GetMessage()).Should()
            .Contain(m => m.Contains("'orphan'") || m.Contains("(\"orphan\")"));
        run.Result.GeneratedTrees.Should().NotContain(t => t.FilePath.Contains("FeatureFlags.g.cs"));
    }

    [Fact]
    public void FlagOutsideEveryModule_ReportsELFLAG004() {
        var run = Run(Preamble +
                      """
                      namespace Sample.Billing {
                          [AppModule("Billing")]
                          public static class BillingModule { }
                      }

                      namespace Elsewhere {
                          [BackendFeatureFlag("stray")]
                          public static class StrayFlags { }
                      }
                      """);

        run.Diagnostics.Should().ContainSingle(d => d.Id == "ELFLAG004");
    }

    [Fact]
    public void BlankFlagName_ReportsELFLAG005() {
        var run = Run(Preamble +
                      """
                      namespace Sample.Billing {
                          [AppModule("Billing")]
                          public static class BillingModule { }

                          [BackendFeatureFlag("  ")]
                          public static class BlankFlags { }
                      }
                      """);

        run.Diagnostics.Should().ContainSingle(d => d.Id == "ELFLAG005");
    }

    [Fact]
    public void ManifestPublishesEveryNamedDeclaration() {
        var compilation = CSharpCompilation.Create(
            "ManifestFlags",
            [CSharpSyntaxTree.ParseText(
                Preamble + BillingModule, new CSharpParseOptions(LanguageVersion.Preview),
                cancellationToken: TestContext.Current.CancellationToken)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ElarionManifestGenerator());

        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();

        var manifest = result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString();
        manifest.Split("Elarion.Manifest.FeatureFlag.v1").Length.Should().Be(4); // beta-reports, dashboard-v2, new-export
    }

    [Fact]
    public void Registry_EmitsTypedKeysNamesAndDescriptorsAcrossReferencedAssemblies() {
        var reference = CompileToImage(Preamble + BillingModule, "Sample.Billing.Lib");

        var run = Run(
            Preamble + UseElarion +
            """
            namespace Sample.Host {
                [AppModule("Host")]
                public static class HostModule { }

                [BackendFeatureFlag("host-flag")]
                public static class HostFlags { }
            }
            """,
            [reference]);

        run.Errors.Should().BeEmpty();
        var registry = Generated(run, "ElarionFeatureFlags.g.cs");
        registry.Should().Contain("public static partial class ElarionFeatureFlags");
        registry.Should().Contain("public const string NewExport = \"new-export\";");
        registry.Should().Contain("public const string HostFlag = \"host-flag\";");
        registry.Should().Contain("public static readonly global::Elarion.Abstractions.Features.FeatureFlagKey BetaReports = new(\"beta-reports\");");
        registry.Should().Contain("Owner = global::Elarion.Abstractions.Features.FeatureFlagOwner.Code,");
        registry.Should().Contain("Module = \"Billing\",");
        registry.Should().Contain("Module = \"Host\",");
        // Sorted by name, so the aggregate is byte-stable.
        registry.IndexOf("\"beta-reports\"", StringComparison.Ordinal)
            .Should().BeLessThan(registry.IndexOf("\"new-export\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_IsByteIdenticalRegardlessOfDeclarationOrder() {
        const string flagsA = "[BackendFeatureFlag(\"alpha\")] [BackendFeatureFlag(\"beta\")] public static class A { }";
        const string flagsB = "[BackendFeatureFlag(\"beta\")] [BackendFeatureFlag(\"alpha\")] public static class A { }";
        string Source(string flags) => Preamble + UseElarion +
                                       "namespace Sample.Billing { [AppModule(\"Billing\")] public static class BillingModule { } " +
                                       flags + " }";

        var first = Run(Source(flagsA));
        var second = Run(Source(flagsB));

        Generated(first, "ElarionFeatureFlags.g.cs").Should().Be(Generated(second, "ElarionFeatureFlags.g.cs"));
        Generated(first, "Sample_Billing_BillingModuleElarionModuleServices.FeatureFlags.g.cs")
            .Should().Be(Generated(second, "Sample_Billing_BillingModuleElarionModuleServices.FeatureFlags.g.cs"));
    }

    [Fact]
    public void Registry_ReportsAccessorCollisionsAsWarningsAndKeepsEveryFlagInAll() {
        var run = Run(Preamble + UseElarion +
                      """
                      namespace Sample.Billing {
                          [AppModule("Billing")]
                          public static class BillingModule { }

                          [BackendFeatureFlag("new-export")]
                          [BackendFeatureFlag("new_export")]
                          public static class Flags { }
                      }
                      """);

        run.Diagnostics.Should().ContainSingle(d => d.Id == "ELFLAG006" && d.Severity == DiagnosticSeverity.Warning);
        var registry = Generated(run, "ElarionFeatureFlags.g.cs");
        registry.Should().Contain("Name = \"new-export\"").And.Contain("Name = \"new_export\"");
    }

    [Fact]
    public void WithoutTheTrigger_NoRegistryIsEmittedButDeclarationsStillRegister() {
        var run = Run(Preamble + BillingModule);

        run.Result.GeneratedTrees.Should().NotContain(t => t.FilePath.EndsWith("ElarionFeatureFlags.g.cs"));
        run.Result.GeneratedTrees.Should().Contain(t => t.FilePath.Contains("FeatureFlags.g.cs"));
    }

    [Fact]
    public void GeneratedRegistryAndRegistrationsCompile() {
        var run = Run(Preamble + UseElarion + BillingModule);

        run.Errors.Should().BeEmpty();
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new FeatureFlagGenerator(), Preamble + UseElarion + BillingModule,
            "FeatureFlagCodeDeclarations", "FeatureFlagBackendDeclarations", "FeatureFlagGateUsages",
            "FeatureFlagVariantUsages", "FeatureFlagReferences", "FeatureFlagsCombined");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new FeatureFlagGenerator(), Preamble + UseElarion + BillingModule,
            "FeatureFlagCodeDeclarations", "FeatureFlagBackendDeclarations");
    }

    private sealed record RunResult(
        GeneratorDriverRunResult Result,
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<Diagnostic> Errors);

    private static RunResult Run(string source, IReadOnlyList<MetadataReference>? extraReferences = null) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = CreateMetadataReferences().Concat(extraReferences ?? []).ToArray();
        Compilation compilation = CSharpCompilation.Create(
            "FeatureFlagGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ModuleDefaultServicesGenerator().AsSourceGenerator(), new FeatureFlagGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var generatorDiagnostics, TestContext.Current.CancellationToken);

        var diagnostics = generatorDiagnostics.Concat(
                outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                    .Where(d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith("ELFLAG", StringComparison.Ordinal)))
            .ToList();
        var errors = outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith("ELFLAG", StringComparison.Ordinal))
            .ToList();
        return new RunResult(driver.GetRunResult(), diagnostics, errors);
    }

    private static string Generated(RunResult run, string fileName) {
        return run.Result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static MetadataReference CompileToImage(string source, string assemblyName) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        Compilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out _, TestContext.Current.CancellationToken);

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
