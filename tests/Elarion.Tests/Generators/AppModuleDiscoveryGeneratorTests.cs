using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class AppModuleDiscoveryGeneratorTests {
    [Fact]
    public void GenerateBootstrapper_CoreModule_IsAlwaysEnabledAndOrderedBeforeFeatures() {
        const string source =
            """
            namespace Elarion.AspNetCore {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class GenerateModuleBootstrapperAttribute : System.Attribute;
            }

            namespace Elarion.Abstractions.Modules {
                public enum AppModuleKind {
                    Feature = 0,
                    Core = 1,
                }

                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute(string name) : System.Attribute {
                    public string Name { get; } = name;
                    public AppModuleKind Kind { get; init; } = AppModuleKind.Feature;
                    public string? DependsOn { get; init; }
                }

            }

            namespace Microsoft.AspNetCore.Routing {
                public interface IEndpointRouteBuilder;
            }

            namespace Microsoft.Extensions.Configuration {
                public interface IConfiguration;

                public static class ConfigurationBinder {
                    public static T GetValue<T>(this IConfiguration configuration, string key, T defaultValue) =>
                        defaultValue;
                }
            }

            namespace Microsoft.Extensions.DependencyInjection {
                public interface IServiceCollection;
            }

            namespace Elarion {
                public static class HandlerSenderServiceCollectionExtensions {
                    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddElarionHandlerSender(
                        Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services;
                }
            }

            namespace Host {
                [Elarion.AspNetCore.GenerateModuleBootstrapper]
                public static partial class ModuleBootstrapper;
            }

            namespace Sample.Modules.Core {
                [Elarion.Abstractions.Modules.AppModule("Core", Kind = Elarion.Abstractions.Modules.AppModuleKind.Core)]
                public static class CoreModule {
                    public static void ConfigureServices(object services, object configuration) {
                    }
                }
            }

            namespace Sample.Modules.AiAgent {
                [Elarion.Abstractions.Modules.AppModule("AiAgent")]
                public static class AiAgentModule {
                    public static void ConfigureServices(object services, object configuration) {
                    }

                    public static void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) {
                    }
                }
            }
            """;

        var generated = GenerateModuleBootstrapperSource(source);

        generated.Should().Contain("global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints");
        generated.Should().Contain("public static void AddElarion(");
        generated.Should().Contain("public static bool IsModuleEnabled(");
        generated.Should().NotContain("public static partial void AddElarion(");

        // The aggregate entry points are emitted as extension methods on their receiver (idiomatic host wiring:
        // services.AddElarion(configuration), endpoints.MapElarionEndpoints(configuration),
        // configuration.IsModuleEnabled(name)).
        generated.Should().Contain(
            "        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,");
        generated.Should().Contain(
            "        this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,");
        generated.Should().Contain(
            "        this global::Microsoft.Extensions.Configuration.IConfiguration configuration,");
        generated.Should().Contain("global::Sample.Modules.Core.CoreModule.ConfigureServices(services, configuration);");
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"AiAgent\"))");
        generated.Should().Contain("global::Sample.Modules.AiAgent.AiAgentModule.MapEndpoints(endpoints);");
        generated.Should().Contain("\"Core\" => true,");
        generated.Should().NotContain("Modules:Core:Enabled");
        generated.Should().Contain("return new string[] { \"Core\", \"AiAgent\" };");

        generated.IndexOf(
                "global::Sample.Modules.Core.CoreModule.ConfigureServices",
                StringComparison.Ordinal)
            .Should()
            .BeLessThan(generated.IndexOf(
                "global::Sample.Modules.AiAgent.AiAgentModule.ConfigureServices",
                StringComparison.Ordinal));

        // Generated defaults are wired unconditionally for core modules and gated for feature modules,
        // and always before the module's optional hand-written ConfigureServices.
        generated.Should().Contain(
            "global::Sample.Modules.Core.CoreModuleElarionModuleServices.ConfigureDefaultServices(services);");
        generated.Should().Contain(
            "global::Sample.Modules.AiAgent.AiAgentModuleElarionModuleServices.ConfigureDefaultServices(services);");
        generated.IndexOf(
                "global::Sample.Modules.Core.CoreModuleElarionModuleServices.ConfigureDefaultServices",
                StringComparison.Ordinal)
            .Should()
            .BeLessThan(generated.IndexOf(
                "global::Sample.Modules.Core.CoreModule.ConfigureServices",
                StringComparison.Ordinal));
    }

    private static string GenerateModuleBootstrapperSource(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "AppModuleDiscoveryGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        // The skeleton generator ships in the same assembly and emits each module's ConfigureDefaultServices
        // sibling that the bootstrapper invokes, so it runs alongside the host generator here.
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new AppModuleDiscoveryGenerator(), new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var emitDiagnostics);
        var result = driver.GetRunResult();

        emitDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var generatedTree = result.GeneratedTrees.Single(
            tree => tree.FilePath.EndsWith(
                "ModuleBootstrapper.g.cs",
                StringComparison.Ordinal));

        return generatedTree.GetText().ToString();
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
