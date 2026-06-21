using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ModuleServiceRegistrationGeneratorTests
{
    [Fact]
    public void GenerateServices_ImplicitContracts_EmitsServiceAndModuleMethods()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IInvoiceNumberGenerator {
                }

                [Elarion.Abstractions.Service]
                public sealed class InvoiceNumberGenerator : IInvoiceNumberGenerator {
                }
            }
            """);

        var result = Generate(source);
        var serviceRegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_InvoiceNumberGenerator.g.cs");
        var moduleRegistrationSource = GetGeneratedSource(result, "BillingServiceExtensions.g.cs");

        serviceRegistrationSource.Should().Contain(
            "typeof(global::Sample.Modules.Billing.Services.IInvoiceNumberGenerator)");
        moduleRegistrationSource.Should().Contain("AddBillingServices");
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.InvoiceNumberGeneratorServiceRegistration.AddInvoiceNumberGeneratorService(services);");
    }

    [Fact]
    public void GenerateServices_UseElarion_EmitsServiceAndModuleMethods()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IInvoiceNumberGenerator {
                }

                [Elarion.Abstractions.Service]
                public sealed class InvoiceNumberGenerator : IInvoiceNumberGenerator {
                }
            }
            """,
            "[assembly: Elarion.Abstractions.UseElarion]");

        var result = Generate(source);
        var moduleRegistrationSource = GetGeneratedSource(result, "BillingServiceExtensions.g.cs");

        moduleRegistrationSource.Should().Contain("AddBillingServices");
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.InvoiceNumberGeneratorServiceRegistration.AddInvoiceNumberGeneratorService(services);");
    }

    [Fact]
    public void GenerateServices_ExplicitContract_OverridesImplicitContracts()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IFirst {
                }

                public interface ISecond {
                }

                [Elarion.Abstractions.Service(typeof(IFirst))]
                public sealed class MultiContractService : IFirst, ISecond {
                }
            }
            """);

        var result = Generate(source);
        var serviceRegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_MultiContractService.g.cs");

        serviceRegistrationSource.Should().Contain("typeof(global::Sample.Modules.Billing.Services.IFirst)");
        serviceRegistrationSource.Should().NotContain("ISecond");
    }

    [Fact]
    public void GenerateServices_HostedSingleton_RegistersHostedServiceLookup()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IQueueRunner {
                }

                [Elarion.Abstractions.Service(typeof(IQueueRunner), Scope = Elarion.Abstractions.ServiceScope.Singleton)]
                public sealed class QueueWorker : Microsoft.Extensions.Hosting.BackgroundService, IQueueRunner {
                    protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var serviceRegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_QueueWorker.g.cs");

        serviceRegistrationSource.Should().Contain(
            "typeof(global::Microsoft.Extensions.Hosting.IHostedService)");
        serviceRegistrationSource.Should().Contain(
            "GetRequiredService<global::Sample.Modules.Billing.Services.IQueueRunner>()");
    }

    [Fact]
    public void GenerateServices_HostedScoped_EmitsScopeDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IQueueRunner {
                }

                [Elarion.Abstractions.Service(typeof(IQueueRunner), Scope = Elarion.Abstractions.ServiceScope.Scoped)]
                public sealed class QueueWorker : Microsoft.Extensions.Hosting.BackgroundService, IQueueRunner {
                    protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELSG001" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateServices_InvalidExplicitContract_EmitsContractDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IFoo {
                }

                public interface IBar {
                }

                [Elarion.Abstractions.Service(typeof(IBar))]
                public sealed class ContractMismatchService : IFoo {
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELSG002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateServices_NoInterfaces_FallsBackToSelfRegistration()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                [Elarion.Abstractions.Service]
                public sealed class PlainUtility {
                }
            }
            """);

        var result = Generate(source);
        var serviceRegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_PlainUtility.g.cs");

        serviceRegistrationSource.Should().Contain(
            "typeof(global::Sample.Modules.Billing.Services.PlainUtility)");
    }

    [Fact]
    public void GenerateServices_PartialDeclarations_DeduplicatesModulesAndServices()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }

                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IInvoiceNumberGenerator {
                }

                [Elarion.Abstractions.Service]
                public sealed partial class InvoiceNumberGenerator : IInvoiceNumberGenerator {
                }

                public sealed partial class InvoiceNumberGenerator {
                }
            }
            """);

        var result = Generate(source);
        var generatedFileCount = result.GeneratedTrees.Count(
            tree => string.Equals(
                Path.GetFileName(tree.FilePath),
                "Sample_Modules_Billing_Services_InvoiceNumberGenerator.g.cs",
                StringComparison.Ordinal));
        var moduleRegistrationSource = GetGeneratedSource(result, "BillingServiceExtensions.g.cs");

        generatedFileCount.Should().Be(1);
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.InvoiceNumberGeneratorServiceRegistration.AddInvoiceNumberGeneratorService(services);");
    }

    [Fact]
    public void GenerateServices_GenericImplementation_EmitsUnsupportedDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IRepository<T> {
                }

                [Elarion.Abstractions.Service]
                public sealed class Repository<T> : IRepository<T> {
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false);

        result.Diagnostics.Any(d => d.Id == "ELSG003" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateServices_NestedServices_UseUniqueRegistrationIdentifiers()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IWorkerA {
                }

                public interface IWorkerB {
                }

                public static class OuterA {
                    [Elarion.Abstractions.Service]
                    public sealed class Worker : IWorkerA {
                    }
                }

                public static class OuterB {
                    [Elarion.Abstractions.Service]
                    public sealed class Worker : IWorkerB {
                    }
                }
            }
            """);

        var result = Generate(source);
        var moduleRegistrationSource = GetGeneratedSource(result, "BillingServiceExtensions.g.cs");
        var outerARegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_OuterA_Worker.g.cs");
        var outerBRegistrationSource = GetGeneratedSource(
            result,
            "Sample_Modules_Billing_Services_OuterB_Worker.g.cs");

        outerARegistrationSource.Should().Contain("public static class OuterA_WorkerServiceRegistration");
        outerARegistrationSource.Should().Contain("typeof(global::Sample.Modules.Billing.Services.OuterA.Worker)");
        outerBRegistrationSource.Should().Contain("public static class OuterB_WorkerServiceRegistration");
        outerBRegistrationSource.Should().Contain("typeof(global::Sample.Modules.Billing.Services.OuterB.Worker)");
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.OuterA_WorkerServiceRegistration.AddOuterA_WorkerService(services);");
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.OuterB_WorkerServiceRegistration.AddOuterB_WorkerService(services);");
    }

    [Fact]
    public void GenerateServices_ServiceSuffixSiblings_KeepUniqueRegistrationIdentifiers()
    {
        var source = CreateSource(
            """
            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }
            }

            namespace Sample.Modules.Billing.Services {
                public interface IFoo {
                }

                public interface IFooService {
                }

                [Elarion.Abstractions.Service]
                public sealed class Foo : IFoo {
                }

                [Elarion.Abstractions.Service]
                public sealed class FooService : IFooService {
                }
            }
            """);

        var result = Generate(source);
        var moduleRegistrationSource = GetGeneratedSource(result, "BillingServiceExtensions.g.cs");

        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.FooServiceRegistration.AddFooService(services);");
        moduleRegistrationSource.Should().Contain(
            "global::Sample.Modules.Billing.Services.FooServiceServiceRegistration.AddFooServiceService(services);");
    }

    [Fact]
    public void GenerateServices_GlobalNamespaceModule_EmitsCompilableRegistration()
    {
        var source = CreateSource(
            """
            [Elarion.Abstractions.Modules.AppModule("Root")]
            public static partial class RootModule {
            }

            public interface IRootService {
            }

            [Elarion.Abstractions.Service]
            public sealed class RootService : IRootService {
            }
            """);

        var result = Generate(source);
        var serviceRegistrationSource = GetGeneratedSource(result, "RootService.g.cs");
        var moduleRegistrationSource = GetGeneratedSource(result, "RootServiceExtensions.g.cs");

        serviceRegistrationSource.Should().NotContain("namespace ;");
        moduleRegistrationSource.Should().NotContain("namespace ;");
        moduleRegistrationSource.Should().Contain(
            "RootServiceServiceRegistration.AddRootServiceService(services);");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateModuleServices]") =>
        $$"""
        {{assemblyTrigger}}

        namespace Elarion.Abstractions {
            public enum ServiceScope {
                Scoped = 0,
                Singleton = 1,
                Transient = 2
            }

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class GenerateModuleServicesAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class UseElarionAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class ServiceAttribute : System.Attribute {
                public ServiceAttribute(params System.Type[] serviceTypes) {
                }

                public ServiceScope Scope { get; init; } = ServiceScope.Scoped;
            }
        }

        namespace Elarion.Abstractions.Modules {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppModuleAttribute : System.Attribute {
                public AppModuleAttribute(string name) {
                    Name = name;
                }

                public string Name { get; }
                public string? DependsOn { get; init; }
            }
        }

        {{testSource}}
        """;

    private static GeneratorDriverRunResult Generate(
        string source,
        bool assertGeneratedOutputCompiles = true)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ModuleServiceRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        // The ConfigureDefaultServices skeleton ships in the same generator assembly and supplies the partial-method
        // declarations the service generator's filler implements, so it must run alongside here.
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new ModuleServiceRegistrationGenerator(), new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);
        var result = driver.GetRunResult();

        var nonGeneratorDiagnostics = generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error &&
                        d.Id is not "ELSG001" and not "ELSG002" and not "ELSG003");
        nonGeneratorDiagnostics.Should().BeEmpty();

        if (assertGeneratedOutputCompiles)
        {
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty();
        }

        return result;
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

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
