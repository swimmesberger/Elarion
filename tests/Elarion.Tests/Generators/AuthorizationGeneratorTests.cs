using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class AuthorizationGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AttachesAuthorizationDecoratorToGuardedHandler() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [RequirePermission("tenants", "write")]
                public sealed class GuardedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_GuardedHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.AuthorizationDecorator<");
        generated.Should().Contain("GetRequiredService<global::Elarion.Abstractions.Authorization.IAuthorizer>()");
        generated.Should().Contain("__handlerMetadata");
        // Authorization is the outermost functional gate, just inside tracing.
        generated.IndexOf("AuthorizationDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("TracingDecorator", StringComparison.Ordinal));

        AssertCompiles(source, generated);
    }

    [Fact]
    public void DoesNotAttachToUnannotatedHandlerByDefault() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record ReadThingQuery(int Id) : IQuery;
                public sealed record ReadThingResponse(string Name);

                public sealed class OpenHandler : IHandler<ReadThingQuery, Result<ReadThingResponse>> {
                    public ValueTask<Result<ReadThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_OpenHandler.g.cs");

        generated.Should().NotContain("AuthorizationDecorator");
    }

    [Fact]
    public void SecureByDefaultAttachesToUnannotatedHandlerButNotAllowAnonymous() {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]
            [assembly: ElarionAuthorizationDefaults]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record ReadThingQuery(int Id) : IQuery;
                public sealed record ReadThingResponse(string Name);

                public sealed class DefaultGuardedHandler : IHandler<ReadThingQuery, Result<ReadThingResponse>> {
                    public ValueTask<Result<ReadThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                }

                public sealed record PublicQuery(int Id) : IQuery;

                [AllowAnonymous]
                public sealed class PublicHandler : IHandler<PublicQuery, Result<ReadThingResponse>> {
                    public ValueTask<Result<ReadThingResponse>> HandleAsync(PublicQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);

        var guarded = GetGenerated(result, "Sample_App_DefaultGuardedHandler.g.cs");
        guarded.Should().Contain("AuthorizationDecorator");
        guarded.Should().Contain("requireAuthenticatedByDefault: true");

        var open = GetGenerated(result, "Sample_App_PublicHandler.g.cs");
        open.Should().NotContain("AuthorizationDecorator");
    }

    [Fact]
    public void ReportsElauth001WhenResponseCannotRepresentFailure() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record PlainQuery(int Id) : IQuery;
                public sealed record PlainResponse(string Name);

                // Bare (non-Result) response: authorization cannot short-circuit -> ELAUTH001.
                [RequirePermission("x", "read")]
                public sealed class PlainHandler : IHandler<PlainQuery, PlainResponse> {
                    public ValueTask<PlainResponse> HandleAsync(PlainQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(new PlainResponse("x"));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELAUTH001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("AuthorizationDecorator");
    }

    [Fact]
    public void AttachesAuthorizationFromBaseHandlerClass() {
        // [RequirePermission] is Inherited = true, so a requirement declared on a BASE handler must attach the
        // decorator to the derived handler — otherwise the derived handler ships with no enforcement (C5).
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [RequirePermission("tenants", "write")]
                public abstract class GuardedBase : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public abstract ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct);
                }

                public sealed class DerivedHandler : GuardedBase {
                    public override ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        var generated = GetGenerated(result, "Sample_App_DerivedHandler.g.cs");

        generated.Should().Contain("global::Elarion.Pipeline.AuthorizationDecorator<");
        AssertCompiles(source, generated);
    }

    [Fact]
    public void AllowAnonymousOnDerivedOverridesBaseRequirement() {
        // [AllowAnonymous] on the derived handler must win over an inherited base requirement, matching the
        // runtime HandlerMetadata GetCustomAttribute(inherit: true) semantics.
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [RequirePermission("tenants", "write")]
                public abstract class GuardedBase : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public abstract ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct);
                }

                [AllowAnonymous]
                public sealed class OpenDerivedHandler : GuardedBase {
                    public override ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var (result, _) = Run(source);
        GetGenerated(result, "Sample_App_OpenDerivedHandler.g.cs").Should().NotContain("AuthorizationDecorator");
    }

    [Fact]
    public void DenyByDefaultExcludesEventConsumerHandlers() {
        // Under [ElarionAuthorizationDefaults] deny-by-default, an event-consumer handler (request : IDomainEvent/
        // IIntegrationEvent) must NOT get the implicit authorization decorator — its delivery scope has no user, so
        // it would fail every consumer (H9). Explicit [Require*] on a consumer still attaches (covered elsewhere).
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Messaging;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]
            [assembly: ElarionAuthorizationDefaults]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record OrderPlaced(int Id) : IIntegrationEvent;

                public sealed class OrderPlacedConsumer : IHandler<OrderPlaced> {
                    public ValueTask<Result> HandleAsync(OrderPlaced request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, _) = Run(source);
        GetGenerated(result, "Sample_App_OrderPlacedConsumer.g.cs").Should().NotContain("AuthorizationDecorator");
    }

    [Fact]
    public void DenyByDefaultStillAttachesToExplicitlyGuardedEventConsumer() {
        // An EXPLICIT [Require*] on an event consumer is a deliberate app choice and must still attach, even though
        // the IMPLICIT deny-by-default does not.
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Messaging;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]
            [assembly: ElarionAuthorizationDefaults]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record OrderPlaced(int Id) : IIntegrationEvent;

                [RequirePermission("orders", "process")]
                public sealed class OrderPlacedConsumer : IHandler<OrderPlaced> {
                    public ValueTask<Result> HandleAsync(OrderPlaced request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, _) = Run(source);
        GetGenerated(result, "Sample_App_OrderPlacedConsumer.g.cs").Should().Contain("AuthorizationDecorator");
    }

    [Fact]
    public void IrrelevantEditReusesPipeline() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [RequirePermission("tenants", "write")]
                public sealed class GuardedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "AuthorizationGeneratorTests",
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
            "AuthorizationGeneratorCompile",
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
