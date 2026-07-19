using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

public sealed class RequestMarkerAnalyzerTests {
    [Fact]
    public async Task SelfTypedMarkerNamingAnotherType_IsReportedAsError() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Query(System.Guid Id)
                    : Elarion.Abstractions.IQuery<Query, Response>;
                public sealed record Response(string Name);

                // Copy-paste slip: Other's marker names Query, not Other.
                public sealed record Other(System.Guid Id)
                    : Elarion.Abstractions.IQuery<Query, Response>;
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELREQ001").Which.Severity
            .Should().Be(DiagnosticSeverity.Error);
        diagnostics.Single(d => d.Id == "ELREQ001").GetMessage().Should().Contain("Other");
    }

    [Fact]
    public async Task SelfTypedMarkerAndMatchingHandler_ReportNothing() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Query(System.Guid Id)
                    : Elarion.Abstractions.IQuery<Query, Response>;
                public sealed record Response(string Name);

                public sealed class Handler
                    : Elarion.Abstractions.IHandler<Query, Elarion.Abstractions.Result<Response>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                        Query request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id.StartsWith("ELREQ"));
    }

    [Fact]
    public async Task HandlerResponseDriftingFromMarker_IsReportedAsWarning() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Query(System.Guid Id)
                    : Elarion.Abstractions.IQuery<Query, Response>;
                public sealed record Response(string Name);
                public sealed record RenamedResponse(string Name);

                // The handler moved to RenamedResponse but the marker still declares Response.
                public sealed class Handler
                    : Elarion.Abstractions.IHandler<Query, Elarion.Abstractions.Result<RenamedResponse>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<RenamedResponse>> HandleAsync(
                        Query request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        var mismatch = diagnostics.Should().ContainSingle(d => d.Id == "ELREQ002").Which;
        mismatch.Severity.Should().Be(DiagnosticSeverity.Warning);
        mismatch.GetMessage().Should().Contain("Handler").And.Contain("Response");
    }

    [Fact]
    public async Task MarkerFreeRequest_HandlerIsNotChecked() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Query(System.Guid Id) : Elarion.Abstractions.IQuery;
                public sealed record Response(string Name);

                public sealed class Handler
                    : Elarion.Abstractions.IHandler<Query, Elarion.Abstractions.Result<Response>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                        Query request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id.StartsWith("ELREQ"));
    }

    [Fact]
    public async Task StreamHandlerItemDriftingFromMarker_IsReportedAsWarning() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Tail(string Topic)
                    : Elarion.Abstractions.IStreamRequest<Tail, string>;

                public sealed class Handler : Elarion.Abstractions.IStreamHandler<Tail, int> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<System.Collections.Generic.IAsyncEnumerable<int>>> HandleAsync(
                        Tail request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELREQ003").Which.Severity
            .Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task StreamMarkerAndMatchingStreamHandler_ReportNothing() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Tail(string Topic)
                    : Elarion.Abstractions.IStreamRequest<Tail, string>;

                public sealed class Handler : Elarion.Abstractions.IStreamHandler<Tail, string> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<System.Collections.Generic.IAsyncEnumerable<string>>> HandleAsync(
                        Tail request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id.StartsWith("ELREQ"));
    }

    [Fact]
    public async Task GenericRequestNamingItsOwnConstruction_IsNotReported() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Page<T>(int Number)
                    : Elarion.Abstractions.IQuery<Page<T>, T>;
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELREQ001");
    }

    [Fact]
    public async Task IntermediateCrtpInterface_IsNotReported() {
        const string source =
            """
            namespace App.Sessions {
                // Application-defined pass-through marker: TSelf stays a type parameter here and the
                // concrete closure is checked where it is bound.
                public interface ITenantQuery<TSelf, TResponse>
                    : Elarion.Abstractions.IQuery<TSelf, TResponse>
                    where TSelf : ITenantQuery<TSelf, TResponse>;

                public sealed record Query(System.Guid Id) : ITenantQuery<Query, string>;
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELREQ001");
    }

    [Fact]
    public async Task UnitHandlerConvenienceForm_MatchesUnitMarker() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Ping
                    : Elarion.Abstractions.ICommand<Ping, Elarion.Abstractions.Results.Unit>;
                public sealed record Renamed
                    : Elarion.Abstractions.ICommand<Renamed, string>;

                public sealed class PingHandler : Elarion.Abstractions.IHandler<Ping> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        Ping request, System.Threading.CancellationToken ct) => default;
                }

                // IHandler<T> is sugar for IHandler<T, Result<Unit>>; Renamed declares string.
                public sealed class RenamedHandler : Elarion.Abstractions.IHandler<Renamed> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        Renamed request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELREQ002" && d.GetMessage().Contains("PingHandler"));
        diagnostics.Should().ContainSingle(d => d.Id == "ELREQ002").Which.GetMessage()
            .Should().Contain("RenamedHandler");
    }

    [Fact]
    public async Task OpenGenericDecorator_IsNotChecked() {
        const string source =
            """
            namespace App.Sessions {
                public sealed class Decorator<TRequest, TResponse>(
                    Elarion.Abstractions.IHandler<TRequest, TResponse> inner)
                    : Elarion.Abstractions.IHandler<TRequest, TResponse> {
                    public System.Threading.Tasks.ValueTask<TResponse> HandleAsync(
                        TRequest request, System.Threading.CancellationToken ct) => inner.HandleAsync(request, ct);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id.StartsWith("ELREQ"));
    }

    [Fact]
    public async Task SecondHandlerWithDifferentResponse_IsFlaggedOnlyOnTheMismatchedType() {
        const string source =
            """
            namespace App.Sessions {
                public sealed record Query(System.Guid Id)
                    : Elarion.Abstractions.IQuery<Query, Response>;
                public sealed record Response(string Name);
                public sealed record Projection(string Name);

                public sealed class CanonicalHandler
                    : Elarion.Abstractions.IHandler<Query, Elarion.Abstractions.Result<Response>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                        Query request, System.Threading.CancellationToken ct) => default;
                }

                public sealed class ProjectionHandler
                    : Elarion.Abstractions.IHandler<Query, Elarion.Abstractions.Result<Projection>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Projection>> HandleAsync(
                        Query request, System.Threading.CancellationToken ct) => default;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELREQ002").Which.GetMessage()
            .Should().Contain("ProjectionHandler");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "RequestMarkerAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new RequestMarkerAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
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
