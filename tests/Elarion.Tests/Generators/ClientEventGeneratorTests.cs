using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Covers <see cref="ClientEventRegistrationGenerator"/> (ADR-0043): per-module topic registration inferred
/// from <c>IClientEvent</c> contracts ({module}.{name}, trailing <c>Event</c> stripped, <c>[ClientEvent]</c>
/// override), subscribe-time requirements read from <c>[RequirePermission]</c>/<c>[RequireRole]</c>, the
/// <c>ConfigureDefaultServices</c> filler wiring, the ELCEV diagnostics, and pipeline cacheability.
/// </summary>
public sealed class ClientEventGeneratorTests {
    private const string Preamble =
        """
        using System;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.ClientEvents;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    [Fact]
    public void RegistersTopicsPerModuleWithInferredNamesAndRequirements() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }

                [RequirePermission("invoices", "read")]
                public sealed record InvoiceChanged : IClientEvent {
                    public required Guid InvoiceId { get; init; }
                }

                // Trailing "Event" suffix strips: importProgress, not importProgressEvent.
                public sealed record ImportProgressEvent : IClientEvent {
                    public required int Processed { get; init; }
                }
            }
            """;

        var result = Generate(source);
        var extensions = GetGenerated(result, "InvoicingClientEventExtensions.g.cs");

        extensions.Should().Contain("public static IServiceCollection AddInvoicingClientEvents(");
        extensions.Should().Contain(
            "events.AddTopic<global::Sample.Invoicing.ImportProgressEvent>(\"invoicing.importProgress\");");
        extensions.Should().Contain(
            "events.AddTopic<global::Sample.Invoicing.InvoiceChanged>(\"invoicing.invoiceChanged\", static topic => topic");
        extensions.Should().Contain(".RequirePermission(\"invoices\", \"read\"));");
        // The ConfigureDefaultServices filler wires it into the module aggregation.
        result.GeneratedTrees
            .Select(tree => tree.GetText().ToString())
            .Should()
            .Contain(text =>
                text.Contains("static partial void AddClientEvents", StringComparison.Ordinal) &&
                text.Contains("AddInvoicingClientEvents(services)", StringComparison.Ordinal));
    }

    [Fact]
    public void ClientEventAttributeOverridesTheFullTopicName() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }

                [ClientEvent("invoicing.legacyName")]
                [RequireRole("admin")]
                public sealed record RenamedContract : IClientEvent {
                    public required Guid Id { get; init; }
                }
            }
            """;

        var extensions = GetGenerated(Generate(source), "InvoicingClientEventExtensions.g.cs");

        extensions.Should().Contain(
            "events.AddTopic<global::Sample.Invoicing.RenamedContract>(\"invoicing.legacyName\", static topic => topic");
        extensions.Should().Contain(".RequireRole(\"admin\"));");
    }

    [Fact]
    public void ReportsElcev001WhenEventNotInAnyModule() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }
            }

            namespace Sample.Outside {
                public sealed record OrphanChanged : IClientEvent {
                    public required Guid Id { get; init; }
                }
            }
            """;

        var diagnostics = RunForDiagnostics(source);

        diagnostics.Any(d => d.Id == "ELCEV001" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void ReportsElcev002OnDuplicateTopicNames() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }

                public sealed record InvoiceChanged : IClientEvent {
                    public required Guid Id { get; init; }
                }

                [ClientEvent("invoicing.invoiceChanged")]
                public sealed record CollidingContract : IClientEvent {
                    public required Guid Id { get; init; }
                }
            }
            """;

        var diagnostics = RunForDiagnostics(source);

        diagnostics.Count(d => d.Id == "ELCEV002" && d.Severity == DiagnosticSeverity.Error).Should().Be(2);
    }

    [Fact]
    public void ReportsElcev003WhenClientEventsPackageIsNotReferenced() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }

                public sealed record InvoiceChanged : IClientEvent {
                    public required Guid Id { get; init; }
                }
            }
            """;

        var diagnostics = RunForDiagnostics(source, excludeClientEventsPackage: true);

        diagnostics.Any(d => d.Id == "ELCEV003" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void IrrelevantEditReusesClientEvents() {
        const string source = Preamble +
            """

            namespace Sample.Invoicing {
                [AppModule("Invoicing")]
                public static partial class InvoicingModule { }

                public sealed record InvoiceChanged : IClientEvent {
                    public required Guid Id { get; init; }
                }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ClientEventRegistrationGenerator(), source, "ClientEventContracts", "ClientEventContractsCombined");
    }

    private static GeneratorDriverRunResult Generate(string source) {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ClientEventGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct)],
            CreateMetadataReferences(excludeClientEventsPackage: false),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The ConfigureDefaultServices skeleton (ModuleDefaultServicesGenerator) declares the partial hooks this
        // generator's filler implements, so both run together.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[]
            {
                new ClientEventRegistrationGenerator().AsSourceGenerator(),
                new ModuleDefaultServicesGenerator().AsSourceGenerator(),
            },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, ct);

        output.GetDiagnostics(ct)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> RunForDiagnostics(string source, bool excludeClientEventsPackage = false) {
        var ct = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create(
            "ClientEventGeneratorDiagnostics",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct)],
            CreateMetadataReferences(excludeClientEventsPackage),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ClientEventRegistrationGenerator());
        return driver.RunGenerators(compilation, ct).GetRunResult().Diagnostics;
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences(bool excludeClientEventsPackage) {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Where(path => !excludeClientEventsPackage ||
                !Path.GetFileName(path).StartsWith("Elarion.ClientEvents", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
