using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ModuleDefaultServicesGeneratorTests
{
    [Fact]
    public void Skeleton_EmitsSiblingClassWithConfigureDefaultServicesAndHooks()
    {
        const string source =
            """
            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }
            """;

        var generated = GenerateSibling(source);

        generated.Should().Contain("public static partial class BillingModuleElarionModuleServices");
        generated.Should().Contain(
            "public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection ConfigureDefaultServices(");
        foreach (var hook in new[] { "AddHandlers", "AddServices", "AddValidators", "AddScheduledJobs", "AddEventConsumers" })
        {
            generated.Should().Contain($"        {hook}(services);");
            generated.Should().Contain(
                $"static partial void {hook}(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services);");
        }
    }

    [Fact]
    public void Skeleton_GlobalNamespaceModule_EmitsCompilableSibling()
    {
        const string source =
            """
            [Elarion.Abstractions.Modules.AppModule("Root")]
            public static class RootModule { }
            """;

        var generated = GenerateSibling(source);

        generated.Should().Contain("public static partial class RootModuleElarionModuleServices");
        generated.Should().NotContain("namespace ");
    }

    [Fact]
    public void Skeleton_NoModules_EmitsNothing()
    {
        const string source =
            """
            namespace Sample.Modules {
                public static class NotAModule { }
            }
            """;

        var result = Run(source);

        result.GeneratedTrees
            .Any(tree => tree.GetText().ToString().Contains("ConfigureDefaultServices"))
            .Should().BeFalse();
    }

    private static string GenerateSibling(string source)
    {
        var result = Run(source);
        return result.GeneratedTrees
            .Select(tree => tree.GetText().ToString())
            .Single(text => text.Contains("ConfigureDefaultServices"));
    }

    private static GeneratorDriverRunResult Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ModuleDefaultServicesGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        generatorDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult();
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
