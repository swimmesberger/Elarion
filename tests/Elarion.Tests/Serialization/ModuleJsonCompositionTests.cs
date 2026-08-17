using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Serialization;

/// <summary>
/// Pins the self-contained module composition contract: a container that only calls a module's generated
/// <c>ConfigureDefaultServices</c> — the composition a test host or any non-host assembly can reach, since the
/// host-assembly <c>ElarionBootstrapper</c> is out of reach by construction — must still resolve
/// <see cref="IElarionJsonSerialization"/> with the module's source-generated JSON context in the chain.
/// Compiles the module with <see cref="ModuleDefaultServicesGenerator"/>, loads it, and executes the emitted code,
/// so a renamed hook or a changed filler body fails here rather than at a consumer's runtime.
/// </summary>
public sealed class ModuleJsonCompositionTests {
    [Fact]
    public void ConfigureDefaultServices_Alone_ContributesTheModuleJsonContext() {
        var assembly = CompileModule();
        var services = new ServiceCollection();

        InvokeConfigureDefaultServices(assembly, services);

        // No AddElarion, no AddElarionJson, no hand-written ConfigureElarionJson: the module's own registration
        // brings the accessor and its context along.
        var serialization = services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
        var info = serialization.GetTypeInfo<ModuleCompositionDto>();

        var json = JsonSerializer.Serialize(new ModuleCompositionDto { Name = "typed-setting" }, info);
        json.Should().Be("{\"name\":\"typed-setting\"}");
        JsonSerializer.Deserialize(json, info).Should().Be(new ModuleCompositionDto { Name = "typed-setting" });
    }

    [Fact]
    public void ConfigureDefaultServices_ComposedWithTheHostContribution_ContributesTheContextOnce() {
        // The real host runs both paths: the module's own contribution and the bootstrapper's
        // GetAllJsonTypeInfoResolvers loop. The chain must look exactly as it did before either existed alone.
        var assembly = CompileModule();
        var services = new ServiceCollection();

        InvokeConfigureDefaultServices(assembly, services);
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(ModuleCompositionJsonContext.Default));

        var options = services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>().Options;

        options.TypeInfoResolverChain
            .Count(resolver => ReferenceEquals(resolver, ModuleCompositionJsonContext.Default))
            .Should().Be(1);
    }

    private static void InvokeConfigureDefaultServices(Assembly assembly, IServiceCollection services) {
        var sibling = assembly.GetType("Sample.Composition.CompositionModuleElarionModuleServices");
        sibling.Should().NotBeNull("the generator emits the sibling class next to the [AppModule] type");

        var configure = sibling!.GetMethod("ConfigureDefaultServices", BindingFlags.Public | BindingFlags.Static);
        configure.Should().NotBeNull();
        configure!.Invoke(null, [services]);
    }

    private static Assembly CompileModule() {
        // The module's hook returns a context owned by this test assembly: what is under test is that the emitted
        // ConfigureDefaultServices calls the hook and routes it into ConfigureElarionJson, not where the context lives.
        const string source =
            """
            namespace Sample.Composition {
                [Elarion.Abstractions.Modules.AppModule("Composition")]
                public static class CompositionModule {
                    public static System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver GetJsonTypeInfoResolver() =>
                        Elarion.Tests.Serialization.ModuleCompositionJsonContext.Default;
                }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ModuleJsonCompositionTests.Dynamic",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create(new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        using var image = new MemoryStream();
        var emitResult = output.Emit(image, cancellationToken: ct);
        emitResult.Success.Should().BeTrue(
            string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(image.ToArray());
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        // The test host's TPA list carries the real Elarion assemblies and this test assembly, so the generated
        // code compiles against exactly what an application compiles against.
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}

/// <summary>A module-owned payload shape; public so the dynamically compiled module can name its context.</summary>
public sealed record ModuleCompositionDto {
    /// <summary>The payload's only member.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>Stands in for a module's generated JSON context.</summary>
[JsonSerializable(typeof(ModuleCompositionDto))]
public sealed partial class ModuleCompositionJsonContext : JsonSerializerContext;
