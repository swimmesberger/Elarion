using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ResiliencePolicyRegistrationGeneratorTests {
    [Fact]
    public void GenerateResiliencePolicies_PolicyWithRetryAndTimeout_EmitsNeutralMetadataRegistration() {
        var source = CreateSource(
            """
            namespace Sample.Policies {
                [Elarion.Abstractions.Resilience.ResiliencePolicy(
                    "invoice-email",
                    MaxRetryAttempts = 4,
                    Delay = "10s",
                    Backoff = Elarion.Abstractions.Resilience.ResilienceBackoffType.Exponential,
                    MaxDelay = "5m",
                    UseJitter = true,
                    Timeout = "30s")]
                public static partial class InvoiceEmailPolicy;
            }
            """);

        var result = Generate(source);
        var generated = GetGeneratedSource(result, "ResiliencePolicyRegistration.g.cs");

        generated.Should().Contain("public const string Name = \"invoice-email\";");
        generated.Should().Contain("ResiliencePolicyReference Reference { get; } = new() { Name = Name };");
        generated.Should().Contain("ResiliencePolicyMetadataRegistration");
        generated.Should().Contain("ResiliencePolicyMetadata");
        generated.Should().Contain("ResilienceRetryOptions");
        generated.Should().Contain("MaxRetryAttempts = 4");
        generated.Should().Contain($"Delay = global::System.TimeSpan.FromTicks({TimeSpan.FromSeconds(10).Ticks}L)");
        generated.Should().Contain("Backoff = global::Elarion.Abstractions.Resilience.ResilienceBackoffType.Exponential");
        generated.Should().Contain($"MaxDelay = global::System.TimeSpan.FromTicks({TimeSpan.FromMinutes(5).Ticks}L)");
        generated.Should().Contain("UseJitter = true");
        generated.Should().Contain($"Timeout = global::System.TimeSpan.FromTicks({TimeSpan.FromSeconds(30).Ticks}L)");
        generated.Should().Contain("AddResiliencePolicyRegistrationGeneratorTestsResiliencePolicies");
        generated.Should().NotContain("AddElarionResilience");
        generated.Should().NotContain("AddResiliencePipeline");
        generated.Should().NotContain("global::Polly");
        generated.Should().NotContain("});\r\n            });");
        generated.Should().NotContain("});\n            });");
        generated.Should().NotContain("System.Reflection");
    }

    [Fact]
    public void GenerateResiliencePolicies_UseElarion_EnablesGenerator() {
        var source = CreateSource(
            """
            namespace Sample.Policies {
                [Elarion.Abstractions.Resilience.ResiliencePolicy("sample", Timeout = "1s")]
                public static partial class SamplePolicy;
            }
            """,
            "[assembly: Elarion.Abstractions.UseElarion]");

        var result = Generate(source);
        var generated = GetGeneratedSource(result, "ResiliencePolicyRegistration.g.cs");

        generated.Should().Contain("public const string Name = \"sample\";");
    }

    [Fact]
    public void GenerateResiliencePolicies_DuplicatePolicyNames_ReportsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Policies {
                [Elarion.Abstractions.Resilience.ResiliencePolicy("duplicate", Timeout = "1s")]
                public static partial class FirstPolicy;

                [Elarion.Abstractions.Resilience.ResiliencePolicy("duplicate", Timeout = "1s")]
                public static partial class SecondPolicy;
            }
            """);

        var result = Generate(source);

        result.Diagnostics
            .Where(diagnostic => diagnostic.Id == "WFRE002")
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public void GenerateResiliencePolicies_InvalidDuration_ReportsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Policies {
                [Elarion.Abstractions.Resilience.ResiliencePolicy("invalid", Timeout = "nope")]
                public static partial class InvalidPolicy;
            }
            """);

        var result = Generate(source);

        result.Diagnostics
            .Where(diagnostic => diagnostic.Id == "WFRE001")
            .Should()
            .ContainSingle();
    }

    private static GeneratorDriverRunResult Generate(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ResiliencePolicyRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should()
            .BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ResiliencePolicyRegistrationGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateResiliencePolicies]") =>
        $$"""
        {{assemblyTrigger}}

        namespace Elarion.Abstractions {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class GenerateResiliencePoliciesAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class UseElarionAttribute : System.Attribute;
        }

        namespace Elarion.Abstractions.Resilience {
            public enum ResilienceBackoffType {
                Constant,
                Linear,
                Exponential
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class ResiliencePolicyAttribute(string name) : System.Attribute {
                public string Name { get; } = name;
                public int MaxRetryAttempts { get; init; } = 3;
                public string Delay { get; init; } = "2s";
                public ResilienceBackoffType Backoff { get; init; } = ResilienceBackoffType.Constant;
                public string? MaxDelay { get; init; }
                public bool UseJitter { get; init; }
                public string? Timeout { get; init; }
            }
        }

        {{testSource}}
        """;
}
