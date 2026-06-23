using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ModuleApiGeneratorTests
{
    [Fact]
    public void DefaultFacade_EmitsMethodPerHandlerForwarderAndRegistration()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                public sealed record GetThingQuery(int Id);
                public sealed record GetThingResult(string Name);

                public sealed class GetThing
                    : Elarion.Abstractions.IHandler<GetThingQuery, GetThingResult> {
                    public System.Threading.Tasks.ValueTask<GetThingResult> HandleAsync(
                        GetThingQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new GetThingResult("x"));
                }

                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public partial interface IThingsApi;
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("public partial interface IThingsApi");
        generated.Should().Contain(
            "global::System.Threading.Tasks.ValueTask<global::Sample.Things.GetThingResult> GetThing(global::Sample.Things.GetThingQuery request, global::System.Threading.CancellationToken ct = default)");
        generated.Should().Contain("internal sealed class IThingsApiForwarder(global::System.IServiceProvider services)");
        generated.Should().Contain(
            "GetRequiredService<global::Elarion.Abstractions.IHandler<global::Sample.Things.GetThingQuery, global::Sample.Things.GetThingResult>>(services).HandleAsync(request, ct);");
        generated.Should().Contain("public static class ThingsModuleApiExtensions");
        generated.Should().Contain(
            "services.TryAddScoped<global::Sample.Things.IThingsApi, global::Sample.Things.IThingsApiForwarder>();");
        generated.Should().Contain(
            "global::Sample.Things.ThingsModuleApiExtensions.AddThingsModuleApi(services);");
        generated.Should().Contain(
            "static partial void AddModuleApi(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    [Fact]
    public void Exclude_OmitsHandlerFromDefaultFacade()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                public sealed record AQuery(int Id);
                public sealed record AResult(string Name);
                public sealed record BQuery(int Id);
                public sealed record BResult(string Name);

                public sealed class Included
                    : Elarion.Abstractions.IHandler<AQuery, AResult> {
                    public System.Threading.Tasks.ValueTask<AResult> HandleAsync(
                        AQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new AResult("a"));
                }

                [Elarion.Abstractions.Modules.ModuleApi(Exclude = true)]
                public sealed class Hidden
                    : Elarion.Abstractions.IHandler<BQuery, BResult> {
                    public System.Threading.Tasks.ValueTask<BResult> HandleAsync(
                        BQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new BResult("b"));
                }

                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public partial interface IThingsApi;
            }
            """);

        var generated = AllGenerated(Generate(source));

        generated.Should().Contain("AResult> Included(");
        generated.Should().NotContain("BResult> Hidden(");
    }

    [Fact]
    public void ScopedFacade_IncludesOnlyTaggedHandlers()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                public sealed record RQuery(int Id);
                public sealed record RResult(string Name);
                public sealed record PQuery(int Id);
                public sealed record PResult(string Name);

                [Elarion.Abstractions.Modules.ModuleApi("Reporting")]
                public sealed class Reportable
                    : Elarion.Abstractions.IHandler<RQuery, RResult> {
                    public System.Threading.Tasks.ValueTask<RResult> HandleAsync(
                        RQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new RResult("r"));
                }

                public sealed class Plain
                    : Elarion.Abstractions.IHandler<PQuery, PResult> {
                    public System.Threading.Tasks.ValueTask<PResult> HandleAsync(
                        PQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new PResult("p"));
                }

                [Elarion.Abstractions.Modules.GenerateModuleApi("Reporting")]
                public partial interface IReportingApi;
            }
            """);

        var generated = AllGenerated(Generate(source));

        generated.Should().Contain("RResult> Reportable(");
        generated.Should().NotContain("PResult> Plain(");
    }

    [Fact]
    public void NonPartialInterface_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public interface IThingsApi { }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELAPI001"]);

        result.Diagnostics.Any(d => d.Id == "ELAPI001" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void FacadeNotInModule_EmitsWarning()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }
            }

            namespace Other.Surface {
                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public partial interface IStrayApi;
            }
            """,
            wrapInModule: false);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELAPI003" && d.Severity == DiagnosticSeverity.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void HandlerInDifferentModule_IsNotIncluded()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public partial interface IThingsApi;
            }

            namespace Sample.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                public sealed record OrderQuery(int Id);
                public sealed record OrderResult(string Name);

                public sealed class GetOrder
                    : Elarion.Abstractions.IHandler<OrderQuery, OrderResult> {
                    public System.Threading.Tasks.ValueTask<OrderResult> HandleAsync(
                        OrderQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new OrderResult("o"));
                }
            }
            """,
            wrapInModule: false);

        var generated = AllGenerated(Generate(source));

        generated.Should().Contain("public partial interface IThingsApi");
        generated.Should().NotContain("OrderResult> GetOrder(");
    }

    [Fact]
    public void ModuleApi_IrrelevantEdit_ReusesPipeline()
    {
        var source = CreateSource(
            """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Things")]
                public static class ThingsModule { }

                public sealed record GetThingQuery(int Id);
                public sealed record GetThingResult(string Name);

                public sealed class GetThing
                    : Elarion.Abstractions.IHandler<GetThingQuery, GetThingResult> {
                    public System.Threading.Tasks.ValueTask<GetThingResult> HandleAsync(
                        GetThingQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(new GetThingResult("x"));
                }

                [Elarion.Abstractions.Modules.GenerateModuleApi]
                public partial interface IThingsApi;
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ModuleApiGenerator(),
            source,
            "Facades",
            "Handlers",
            "ModuleApiCombined");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.UseElarion]",
        bool wrapInModule = true)
    {
        var moduleDeclaration = wrapInModule && !testSource.Contains("AppModule(")
            ? """
            namespace Sample.Things {
                [Elarion.Abstractions.Modules.AppModule("Sample")]
                public static class GeneratedTestModule { }
            }
            """
            : "";

        return $"""
        {assemblyTrigger}

        {moduleDeclaration}

        {testSource}
        """;
    }

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));

    private static GeneratorDriverRunResult Generate(
        string source,
        bool assertGeneratedOutputCompiles = true,
        string[]? allowedDiagnosticIds = null)
    {
        var allowedIds = new HashSet<string>(allowedDiagnosticIds ?? []);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ModuleApiGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new ModuleApiGenerator(), new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);
        var result = driver.GetRunResult();

        generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error && !allowedIds.Contains(d.Id))
            .Should().BeEmpty();

        if (assertGeneratedOutputCompiles)
        {
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty();
        }

        return result;
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
