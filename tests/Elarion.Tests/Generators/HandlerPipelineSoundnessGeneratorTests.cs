using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Regression coverage for pipeline-attachment soundness: cache-key type validation (H6/M12), event-consumer
/// exclusions for caching (H18) and resilience (H17), and the cache-invalidation Global default (H16).
/// </summary>
public sealed class HandlerPipelineSoundnessGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Caching;
        using Elarion.Abstractions.Messaging;
        using Elarion.Abstractions.Modules;
        using Elarion.Abstractions.Resilience;

        [assembly: UseElarion]
        """;

    [Fact]
    public void CacheKey_UnsupportedPropertyType_ReportsElcache006() {
        // A collection property has no stable key formatting: int[] would format as object.ToString() (a
        // per-instance reference), colliding every value into one key — a cross-request cache leak.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record TagQuery(int[] Tags) : IQuery;
                public sealed record TagResponse(string Name);

                [Cacheable("sample:tags", DurationSeconds = 60)]
                public sealed class TagHandler : IHandler<TagQuery, Result<TagResponse>> {
                    public ValueTask<Result<TagResponse>> HandleAsync(TagQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<TagResponse>.Success(new TagResponse("x")));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELCACHE006" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_TagHandler.g.cs")
            .Should().NotContain("HandlerCacheKey.Part(\"Tags\"");
    }

    [Fact]
    public void CacheKey_SupportedScalarTypes_AreEmitted() {
        // The sound whitelist (Guid, enum, Nullable<int>, DateTime, string) all participate in the key.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public enum Kind { A, B }

                public sealed record ScalarQuery(
                    Guid Id, Kind Kind, int? Count, DateTime When, string Name) : IQuery;
                public sealed record ScalarResponse(string Name);

                [Cacheable("sample:scalars", DurationSeconds = 60)]
                public sealed class ScalarHandler : IHandler<ScalarQuery, Result<ScalarResponse>> {
                    public ValueTask<Result<ScalarResponse>> HandleAsync(ScalarQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ScalarResponse>.Success(new ScalarResponse("x")));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELCACHE006").Should().BeFalse();
        var generated = GetGenerated(result, "Sample_App_ScalarHandler.g.cs");
        generated.Should().Contain("HandlerCacheKey.Part(\"Id\", request.Id)");
        generated.Should().Contain("HandlerCacheKey.Part(\"Kind\", request.Kind)");
        generated.Should().Contain("HandlerCacheKey.Part(\"Count\", request.Count)");
        AssertCompiles(source, generated);
    }

    [Fact]
    public void CacheKey_KeyPropertiesNameNotFound_ReportsElcache007() {
        // A KeyProperties name that matches no request property would be silently dropped from the key.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record ReadQuery(int Id) : IQuery;
                public sealed record ReadResponse(string Name);

                [Cacheable("sample:read", DurationSeconds = 60, KeyProperties = new[] { "Missing" })]
                public sealed class ReadHandler : IHandler<ReadQuery, Result<ReadResponse>> {
                    public ValueTask<Result<ReadResponse>> HandleAsync(ReadQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadResponse>.Success(new ReadResponse("x")));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELCACHE007" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void Cacheable_OnEventConsumer_ReportsElcache005AndDoesNotAttach() {
        // A fan-out event consumer must not be cacheable: a cached Result<Unit> would skip the side effect on a
        // legitimate re-delivery (H18).
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record ThingHappened(int Id) : IIntegrationEvent;

                [Cacheable("sample:things", DurationSeconds = 60)]
                public sealed class ThingConsumer : IHandler<ThingHappened> {
                    public ValueTask<Result> HandleAsync(ThingHappened request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELCACHE005" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_ThingConsumer.g.cs").Should().NotContain("CacheDecorator");
    }

    [Fact]
    public void Resilient_OnDomainEventConsumer_ReportsElpipe003AndDoesNotAttach() {
        // A domain-event consumer runs inline in the publisher's transaction, so a Polly retry would re-apply
        // tracked mutations. [Resilient] must be rejected (H17).
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record OrderPlaced(int Id) : IDomainEvent;

                [Resilient("retry-3x")]
                public sealed class OrderPlacedConsumer : IHandler<OrderPlaced> {
                    public ValueTask<Result> HandleAsync(OrderPlaced request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELPIPE003" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_OrderPlacedConsumer.g.cs").Should().NotContain("ResilienceDecorator");
    }

    [Fact]
    public void Resilient_OnIntegrationEventConsumer_IsAttached() {
        // Integration-event consumers run on a fresh post-commit scope, so [Resilient] is legitimate.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record OrderShipped(int Id) : IIntegrationEvent;

                [Resilient("retry-3x")]
                public sealed class OrderShippedConsumer : IHandler<OrderShipped> {
                    public ValueTask<Result> HandleAsync(OrderShipped request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELPIPE003").Should().BeFalse();
        GetGenerated(result, "Sample_App_OrderShippedConsumer.g.cs").Should().Contain("ResilienceDecorator");
    }

    [Fact]
    public void CacheInvalidate_DefaultScopeIsGlobal() {
        // The default [CacheInvalidate] scope is Global (=1): the mutating caller is usually not the user whose
        // cached read must be evicted, so over-invalidation is the safe default (H16).
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record EditCommand(int Id) : ICommand;
                public sealed record EditResponse(string Name);

                [CacheInvalidate("sample:read")]
                public sealed class EditHandler : IHandler<EditCommand, Result<EditResponse>> {
                    public ValueTask<Result<EditResponse>> HandleAsync(EditCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<EditResponse>.Success(new EditResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_EditHandler.g.cs");

        // HandlerCacheScope.Global == 1.
        generated.Should().Contain("(global::Elarion.Abstractions.Caching.HandlerCacheScope)1");
        AssertCompiles(source, generated);
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "HandlerPipelineSoundnessGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static void AssertCompiles(string source, string generated) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "HandlerPipelineSoundnessCompile",
            [CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(generated, parseOptions)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
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
