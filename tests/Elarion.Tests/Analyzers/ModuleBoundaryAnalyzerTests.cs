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
    public async Task ReferencingForeignPlainType_IsNotGated()
    {
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
