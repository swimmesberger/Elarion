using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// The inbox (ADR-0022): the handler generator attaches a Consumer-scoped idempotency decorator to every
/// handler-form integration-event consumer by default, keyed on the delivered message id.
/// </summary>
public sealed class InboxGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Idempotency;
        using Elarion.Abstractions.Messaging;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void AttachesInboxToIntegrationEventConsumerByDefault() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record InvoiceCreated(int Id) : IIntegrationEvent;

                [ConsumeEvent]
                public sealed class SendInvoiceEmail : IHandler<InvoiceCreated> {
                    public ValueTask<Result> HandleAsync(InvoiceCreated request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);
        var generated = GetGenerated(result, "Sample_App_SendInvoiceEmail.g.cs");

        diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        generated.Should().Contain("global::Elarion.Pipeline.IdempotencyDecorator<");
        // Consumer scope, message-id key semantics: no fingerprint, WaitThenReplay, pass-through without a seed.
        generated.Should().Contain("IdempotencyScope)2");
        generated.Should().Contain("public bool KeyRequired => false;");
        generated.Should().Contain("public bool Fingerprint => false;");
        generated.Should().Contain("IdempotencyConflictBehavior)1");
        // The owner discriminator is the consumer's identity, baked in at compile time; retention is the fixed
        // framework default (a transport-scoped invariant, not a per-consumer knob).
        generated.Should().Contain("public string? Owner => \"Sample.App.SendInvoiceEmail\";");
        generated.Should().Contain("global::System.TimeSpan.FromHours(24)");
        // Soft attach: without AddElarionIdempotency the consumer runs un-deduped instead of failing resolution,
        // and the delivery scope has no caller, so ICurrentUser is soft-resolved too.
        generated.Should().Contain("if (sp.GetService<global::Elarion.Abstractions.Idempotency.IIdempotencyStore>() is { } __inboxStore)");
        generated.Should().Contain("sp.GetService<global::Elarion.Abstractions.Identity.ICurrentUser>()");
        // Result<Unit> stores the success flag only — Unit is registered in no JSON context, so the payload
        // methods must never call GetTypeInfo(typeof(Unit)).
        generated.Should().NotContain("GetTypeInfo(typeof(global::Elarion.Abstractions.Results.Unit))");
        generated.Should().Contain("global::Elarion.Abstractions.Results.Unit.Value");

        AssertCompiles(source, generated);
    }

    [Fact]
    public void AllowDuplicatesRestoresPlainPipeline() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record InvoiceCreated(int Id) : IIntegrationEvent;

                [ConsumeEvent]
                [AllowDuplicates] // conditional state transition — converges by itself
                public sealed class SelfDedupingConsumer : IHandler<InvoiceCreated> {
                    public ValueTask<Result> HandleAsync(InvoiceCreated request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        GetGenerated(result, "Sample_App_SelfDedupingConsumer.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void DoesNotAttachToDomainEventConsumer() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record LineAdded(int Id) : IDomainEvent;

                [ConsumeEvent]
                public sealed class RecalculateTotals : IHandler<LineAdded> {
                    public ValueTask<Result> HandleAsync(LineAdded request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Should().BeEmpty();
        GetGenerated(result, "Sample_App_RecalculateTotals.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void ReportsElinbx001OnNonIntegrationEventHandler() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record PayCommand(int Id) : ICommand;

                [AllowDuplicates]
                public sealed class PayHandler : IHandler<PayCommand, Result<string>> {
                    public ValueTask<Result<string>> HandleAsync(PayCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<string>.Success("r"));
                }
            }
            """;

        var (result, diagnostics) = Run(source);

        diagnostics.Any(d => d.Id == "ELINBX001" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
        GetGenerated(result, "Sample_App_PayHandler.g.cs").Should().NotContain("IdempotencyDecorator");
    }

    [Fact]
    public void IrrelevantEditReusesInboxPipeline() {
        const string source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed record InvoiceCreated(int Id) : IIntegrationEvent;

                [ConsumeEvent]
                public sealed class SendInvoiceEmail : IHandler<InvoiceCreated> {
                    public ValueTask<Result> HandleAsync(InvoiceCreated request, CancellationToken ct) =>
                        ValueTask.FromResult(Result.Success());
                }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(), source, "Handlers");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "InboxGeneratorTests",
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
            "InboxGeneratorCompile",
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
