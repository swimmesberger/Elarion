using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class HandlerRegistrationGeneratorTests {
    [Fact]
    public void GenerateRegistration_HandlerWithoutPipelineAttribute_UsesAssemblyPipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("TransactionDecorator");
        generated.Should().Contain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_ModulePipelineAttribute_OverridesAssemblyPipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "[Sample.Pipeline.ReadOnlyPipeline]",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().NotContain("TransactionDecorator");
        generated.Should().NotContain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_HandlerPipelineAttribute_OverridesModulePipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "[Sample.Pipeline.ReadOnlyPipeline]",
            handlerPipelineAttribute: "[Sample.Pipeline.DefaultPipeline]");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("TransactionDecorator");
        generated.Should().Contain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_UseElarion_EmitsModuleAggregator() {
        var source = CreateSource(
            """
            [assembly: Elarion.Abstractions.UseElarion]
            [assembly: Sample.Pipeline.DefaultPipeline]
            """,
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var result = GenerateHandlerRegistrationRunResult(source);
        var generated = GetGeneratedSource(result, "SalesHandlerExtensions.g.cs");

        generated.Should().Contain("public static IServiceCollection AddSalesHandlers(");
        generated.Should().Contain("Sample.Modules.Sales.Handlers.CreateOrderRegistration.AddCreateOrder(services, lifetime);");
    }

    [Fact]
    public void GenerateRegistration_CacheableHandler_EmitsCacheDecoratorAndPolicy() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute:
            """
            [Sample.Pipeline.ReadOnlyPipeline]
            [Elarion.Abstractions.Caching.Cacheable(
                "sample:sales",
                DurationSeconds = 120,
                Scope = Elarion.Abstractions.Caching.HandlerCacheScope.Global)]
            """);

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("CacheDecorator");
        generated.Should().Contain("CreateOrderCachePolicy");
        generated.Should().Contain("TimeSpan.FromSeconds(120)");
        generated.Should().Contain("\"sample:sales\"");
        generated.Should().Contain("HandlerCacheKey.Part(\"CustomerId\", request.CustomerId)");
        generated.IndexOf("CacheDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ValidationDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_CacheInvalidatingHandler_EmitsInvalidationDecoratorOutermost() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute:
            """
            [Elarion.Abstractions.Caching.CacheInvalidate(
                "sample:sales",
                Scope = Elarion.Abstractions.Caching.HandlerCacheScope.Global)]
            """);

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("CacheInvalidationDecorator");
        generated.Should().Contain("CreateOrderCacheInvalidationPolicy");
        generated.Should().Contain("\"sample:sales\"");
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("CacheInvalidationDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_ResilientHandler_EmitsResilienceDecoratorOutsidePipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: """[Elarion.Abstractions.Resilience.Resilient("invoice-email")]""");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("ResilienceDecorator");
        generated.Should().NotContain("AddElarionResilience");
        generated.Should().Contain("IResiliencePipelineRunner");
        generated.Should().Contain("ResiliencePolicyReference { Name = \"invoice-email\" }");
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ResilienceDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_AnyHandler_EmitsTracingDecoratorOutermost() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        // Tracing is applied unconditionally to every handler, with no opt-in attribute.
        generated.Should().Contain("global::Elarion.Abstractions.Diagnostics.TracingDecorator<");
        generated.Should().Contain("\"CreateOrder\"");
        // Tracing is emitted last so its span parents the pipeline decorators.
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("TracingDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_HandlerWithoutPipeline_StillEmitsTracingDecorator() {
        var source = CreateSource(
            assemblyPipelineAttribute: "",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        // Default-on: tracing wraps the handler even when no other decorator applies.
        generated.Should().Contain("global::Elarion.Abstractions.Diagnostics.TracingDecorator<");
        generated.Should().NotContain("TransactionDecorator");
        generated.Should().NotContain("ValidationDecorator");
    }

    private static string GenerateHandlerRegistrationSource(string source) {
        var result = GenerateHandlerRegistrationRunResult(source);
        return GetGeneratedSource(result, "Sample_Modules_Sales_Handlers_CreateOrder.g.cs");
    }

    private static GeneratorDriverRunResult GenerateHandlerRegistrationRunResult(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "PipelineHierarchyGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return result;
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
        string assemblyPipelineAttribute,
        string modulePipelineAttribute,
        string handlerPipelineAttribute) =>
        $$"""
        {{assemblyPipelineAttribute}}

        namespace Elarion.Abstractions {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class UseElarionAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class GenerateModuleHandlersAttribute : System.Attribute;

            public interface IHandler<TRequest, TResponse> {
                System.Threading.Tasks.ValueTask<TResponse> HandleAsync(
                    TRequest request,
                    System.Threading.CancellationToken ct);
            }

            public readonly record struct Result<T>(T Value);
        }

        namespace Elarion.Abstractions.Caching {
            public enum HandlerCacheScope {
                CurrentUser = 0,
                Global = 1,
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class CacheableAttribute(params string[] tags) : System.Attribute {
                public string[] Tags { get; } = tags;
                public int DurationSeconds { get; init; } = 60;
                public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;
                public string[] KeyProperties { get; init; } = [];
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class CacheInvalidateAttribute(params string[] tags) : System.Attribute {
                public string[] Tags { get; } = tags;
                public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;
            }
        }

        namespace Elarion.Abstractions.Resilience {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class ResilientAttribute(string policyName) : System.Attribute {
                public string PolicyName { get; } = policyName;
            }
        }

        namespace Sample.Decorators {
            public sealed class TransactionDecorator<TRequest, TResponse> {
                public TransactionDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }

            public sealed class DbConstraintDecorator<TRequest, TResponse> {
                public DbConstraintDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }

            public sealed class ValidationDecorator<TRequest, TResponse> {
                public ValidationDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }
        }

        namespace Elarion.Abstractions.Pipeline {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class DecoratorListAttribute(params System.Type[] decorators) : System.Attribute {
                public System.Type[] Decorators { get; } = decorators;
            }
        }

        namespace Sample.Pipeline {
            [Elarion.Abstractions.Pipeline.DecoratorList(
                typeof(Sample.Decorators.TransactionDecorator<,>),
                typeof(Sample.Decorators.DbConstraintDecorator<,>),
                typeof(Sample.Decorators.ValidationDecorator<,>))]
            [System.AttributeUsage(
                System.AttributeTargets.Assembly | System.AttributeTargets.Class,
                Inherited = false,
                AllowMultiple = false)]
            public sealed class DefaultPipelineAttribute : System.Attribute;

            [Elarion.Abstractions.Pipeline.DecoratorList(typeof(Sample.Decorators.ValidationDecorator<,>))]
            [System.AttributeUsage(
                System.AttributeTargets.Assembly | System.AttributeTargets.Class,
                Inherited = false,
                AllowMultiple = false)]
            public sealed class ReadOnlyPipelineAttribute : System.Attribute;
        }

        namespace Elarion.Abstractions.Modules {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppModuleAttribute(string name) : System.Attribute {
                public string Name { get; } = name;
            }
        }

        namespace Sample.Modules.Sales {
            [Elarion.Abstractions.Modules.AppModule("Sales")]
            {{modulePipelineAttribute}}
            public static partial class SalesModule;
        }

        namespace Sample.Modules.Sales.Handlers {
            {{handlerPipelineAttribute}}
            public sealed class CreateOrder
                : Elarion.Abstractions.IHandler<CreateOrder.Command, Elarion.Abstractions.Result<CreateOrder.Response>> {
                public sealed record Command {
                    public required string CustomerId { get; init; }
                }

                public sealed record Response;

                public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                    Command request,
                    System.Threading.CancellationToken ct) =>
                    default;
            }
        }
        """;
}
