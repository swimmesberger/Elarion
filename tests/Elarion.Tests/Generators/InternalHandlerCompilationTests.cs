using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// A handler need not be public: an <c>internal</c> handler, its request, its response and the module-private service
/// it injects compile with every generator of the package, so module-private services stay internal (no CS0051).
/// </summary>
public sealed class InternalHandlerCompilationTests {
    [Fact]
    public void InternalHandler_WithAnInternalService_CompilesWithEveryGenerator() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]

            namespace Sample.Modules.Sales {
                [AppModule("Sales")]
                public static class SalesModule { }

                internal sealed class PriceList {
                    public decimal PriceOf(string sku) => sku.Length;
                }

                [Handler("sales.price")]
                internal sealed class GetPrice(PriceList prices) : IHandler<GetPrice.Query, Result<GetPrice.Response>> {
                    internal sealed record Query(string Sku) : IQuery<Query, Response>;

                    internal sealed record Response(decimal Price);

                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) {
                        return ValueTask.FromResult<Result<Response>>(new Response(prices.PriceOf(request.Sku)));
                    }
                }
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "InternalHandlerCompilationTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSpecificDiagnosticOptions([new KeyValuePair<string, ReportDiagnostic>("CS1591", ReportDiagnostic.Suppress)]));
        var generators = typeof(HandlerRegistrationGenerator).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(type)
                                            && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(type => ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator())
            .ToArray();

        CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, ct);

        generatorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .Should().BeEmpty();
        // The handler is registered and exposed as its JSON-RPC method like a public one.
        output.SyntaxTrees.Should().Contain(tree => tree.GetText(ct).ToString().Contains("\"sales.price\""));
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => SharedMetadataReferences.FromFile(path))
            .ToArray();
    }
}
