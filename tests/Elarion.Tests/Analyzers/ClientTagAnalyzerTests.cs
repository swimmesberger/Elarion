using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

public sealed class ClientTagAnalyzerTests {
    // The cases the generated TypeScript client's parseTimeoutTag is tested with (generate.test.ts), linked from
    // src/elarion-jsonrpc-client-generator/tests: the build refuses exactly the values that client ignores.
    private static readonly TimeoutTagCases Cases = LoadCases();

    [Fact]
    public async Task TimeoutTags_AreRefusedExactlyWhereTheGeneratedClientIgnoresThem() {
        var values = Cases.Valid.Select(c => c.Value).Concat(Cases.Invalid).ToArray();
        var source = new StringBuilder("namespace App.Tags {\n");
        for (var i = 0; i < values.Length; i++)
            source.Append("    [Elarion.Abstractions.ClientTag(\"timeout\", ")
                .Append(SymbolDisplay.FormatLiteral(values[i], true))
                .Append(")] public sealed class Case").Append(i).Append(";\n");
        source.Append('}');

        var diagnostics = await AnalyzeAsync(source.ToString());

        var refused = diagnostics.Where(d => d.Id == "ELRPC013")
            .Select(d => values[ClassIndex(d)])
            .ToArray();
        refused.Should().BeEquivalentTo(Cases.Invalid);
        diagnostics.Where(d => d.Id == "ELRPC013")
            .Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task InvalidTimeoutTag_IsReportedOnTheAttributeAndNamesTheValue() {
        const string source =
            """
            namespace App.Imports {
                [Elarion.Abstractions.ClientTag("viewAs", "refused")]
                [Elarion.Abstractions.ClientTag("timeout", "3 minutes")]
                public sealed class ImportStatementsHandler;
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "ELRPC013").Which;
        diagnostic.GetMessage().Should().Contain("\"3 minutes\"").And.Contain("ImportStatementsHandler");
        diagnostic.Location.SourceTree!.ToString().Substring(diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length)
            .Should().Be("Elarion.Abstractions.ClientTag(\"timeout\", \"3 minutes\")");
    }

    [Fact]
    public async Task OtherKeysAndANullTimeout_AreReadAsTheClientReadsThem() {
        const string source =
            """
            namespace App.Imports {
                // Other keys are free text; the key is case-sensitive like the client's tags.timeout.
                [Elarion.Abstractions.ClientTag("retry", "never")]
                [Elarion.Abstractions.ClientTag("Timeout", "soon")]
                public sealed class Free;

                [Elarion.Abstractions.ClientTag("timeout", null!)]
                public abstract class NullTimeoutBase;
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELRPC013").Which.GetMessage()
            .Should().Contain("NullTimeoutBase");
    }

    private static int ClassIndex(Diagnostic diagnostic) {
        var message = diagnostic.GetMessage();
        var start = message.IndexOf("'Case", StringComparison.Ordinal) + "'Case".Length;
        var end = message.IndexOf('\'', start);
        return int.Parse(message[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ClientTagAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ClientTagAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => SharedMetadataReferences.FromFile(path))
            .ToArray();
    }

    private static TimeoutTagCases LoadCases() {
        var path = Path.Combine(AppContext.BaseDirectory, "Analyzers", "Fixtures", "timeout-tag-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var valid = document.RootElement.GetProperty("valid").EnumerateArray()
            .Select(c => new TimeoutTagCase(c.GetProperty("value").GetString()!,
                c.GetProperty("milliseconds").GetDouble()))
            .ToArray();
        var invalid = document.RootElement.GetProperty("invalid").EnumerateArray()
            .Select(c => c.GetString()!)
            .ToArray();
        return new TimeoutTagCases(valid, invalid);
    }

    private sealed record TimeoutTagCase(string Value, double Milliseconds);

    private sealed record TimeoutTagCases(IReadOnlyList<TimeoutTagCase> Valid, IReadOnlyList<string> Invalid);
}
