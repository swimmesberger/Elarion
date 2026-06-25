using System.Collections.Immutable;
using System.Threading.Tasks;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

public sealed class ModuleBoundaryAnalyzerTests
{
    [Fact]
    public async Task InjectingForeignInternalService_IsReported()
    {
        const string source =
            """
            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                [Elarion.Abstractions.Service]
                public sealed class BillingService { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Billing.BillingService billing) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task ReferencingCoreModuleInternal_IsStillReported()
    {
        // A foundation (Core) module gets no exemption: the analyzer reads only the module name, never
        // Kind. Knowing what a module exports is valuable, so even Core publishes a [ModuleContract] (or a
        // platform port outside every module) for its cross-module surface, like any other module.
        const string source =
            """
            namespace App.Core {
                [Elarion.Abstractions.Modules.AppModule("Core", Kind = Elarion.Abstractions.Modules.AppModuleKind.Core)]
                public static class CoreModule { }

                [Elarion.Abstractions.Service]
                public sealed class CoreService { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Core.CoreService core) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().Contain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task InjectingForeignModuleContract_IsAllowed()
    {
        const string source =
            """
            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                [Elarion.Abstractions.Modules.ModuleContract]
                public interface IBillingApi { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Billing.IBillingApi billing) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task SameModuleReference_IsAllowed()
    {
        const string source =
            """
            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                [Elarion.Abstractions.Service]
                public sealed class BillingRepository { }

                [Elarion.Abstractions.Service]
                public sealed class BillingService {
                    public BillingService(BillingRepository repository) { }
                }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task ReferencingForeignModuleLocalEntity_IsReported()
    {
        // An entity placed *inside* a module's namespace is owned by that module (the way a mini bounded
        // context earns data ownership), so referencing it from another module trips ELMOD002. Contrast
        // ReferencingSharedKernelEntity_IsNotGated, where the entity lives outside every module.
        const string source =
            """
            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed class Invoice { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Billing.Invoice invoice) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().Contain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task ReferencingSharedKernelEntity_IsNotGated()
    {
        // The rule is location-based: an entity outside every [AppModule] (the shared kernel, e.g.
        // App.Persistence) is shared data and freely referenceable from any module.
        const string source =
            """
            namespace App.Persistence {
                public sealed class Customer { }
            }

            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Persistence.Customer customer) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task InjectingForeignEntityConfiguration_IsReported()
    {
        // The configuration class itself is module-internal infrastructure.
        const string source =
            """
            namespace Elarion.EntityFrameworkCore {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class EntityConfigurationAttribute : System.Attribute {
                    public EntityConfigurationAttribute(params string[] scopes) { }
                }
            }

            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed class Invoice { }

                [Elarion.EntityFrameworkCore.EntityConfiguration]
                public sealed class InvoiceConfiguration
                    : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Invoice> {
                    public void Configure(
                        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Invoice> builder) { }
                }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Billing.InvoiceConfiguration configuration) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().Contain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task ReferencingForeignModuleLocalPlainType_IsReported()
    {
        // A plain type owned by another module is module-internal too — sharing it means moving it out of
        // the module (to the shared kernel) or publishing a [ModuleContract], not depending on it directly.
        const string source =
            """
            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed record Money(decimal Amount);
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Billing.Money price) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().Contain(d => d.Id == "ELMOD002");
    }

    [Fact]
    public async Task ReferencingSharedKernelPlainType_IsNotGated()
    {
        // A platform-capability port (or any value type) outside every module is shareable by location —
        // this is the adapter/port pattern's home, distinct from a [ModuleContract].
        const string source =
            """
            namespace App.Abstractions {
                public interface IClock { }
            }

            namespace App.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace App.Orders {
                [Elarion.Abstractions.Modules.AppModule("Orders")]
                public static class OrdersModule { }

                [Elarion.Abstractions.Service]
                public sealed class OrdersService {
                    public OrdersService(App.Abstractions.IClock clock) { }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELMOD002");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ModuleBoundaryAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ModuleBoundaryAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
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
