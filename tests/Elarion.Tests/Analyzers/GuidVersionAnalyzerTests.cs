using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

/// <summary>
/// Covers ELID001 (<see cref="GuidVersionAnalyzer"/>): every <c>System.Guid.NewGuid()</c> call site is flagged
/// with the <c>Guid.CreateVersion7()</c> remedy in the message, look-alikes on other types are not, and an
/// explicit <c>#pragma</c> suppression is honoured — that suppression is the designed escape for the id that
/// must stay unpredictable.
/// </summary>
public sealed class GuidVersionAnalyzerTests {
    [Fact]
    public async Task Flags_EveryNewGuidCallSite() {
        const string source =
            """
            using System;

            namespace Sample {
                public sealed class Ids {
                    public Guid Expression => Guid.NewGuid();

                    public Guid Statement() {
                        var id = Guid.NewGuid();
                        return id;
                    }

                    public string Argument() => Describe(Guid.NewGuid());

                    public Guid Initialized { get; } = Guid.NewGuid();

                    private static string Describe(Guid value) => value.ToString();
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Where(d => d.Id == "ELID001").Should().HaveCount(4);
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Warning);

        // The remedy travels with the message: there is no code fix (the analyzer assembly may not reference
        // Microsoft.CodeAnalysis.Workspaces — RS1038), so the replacement has to be readable in the warning.
        var message = diagnostics.First(d => d.Id == "ELID001").GetMessage();
        message.Should().Contain("Guid.CreateVersion7()").And.Contain("unpredictable");
    }

    [Fact]
    public async Task DoesNotFlag_CreateVersion7_OrOtherGuidMembers() {
        const string source =
            """
            using System;

            namespace Sample {
                public sealed class Ids {
                    public Guid Preferred() => Guid.CreateVersion7();
                    public Guid Parsed() => Guid.Parse("00000000-0000-0000-0000-000000000000");
                    public Guid Empty() => Guid.Empty;
                    public Guid At(DateTimeOffset stamp) => Guid.CreateVersion7(stamp);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELID001");
    }

    [Fact]
    public async Task DoesNotFlag_NewGuidOnAnotherType() {
        const string source =
            """
            using System;

            namespace Sample {
                public static class Correlation {
                    public static Guid NewGuid() => Guid.CreateVersion7();
                }

                public sealed class Ids {
                    public Guid FromOurHelper() => Correlation.NewGuid();
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELID001");
    }

    [Fact]
    public async Task RespectsPragmaSuppression() {
        const string source =
            """
            using System;

            namespace Sample {
                public sealed class Tokens {
                    public Guid ShareLink() {
            #pragma warning disable ELID001 // A share token must be unpredictable; v7 leaks its creation instant.
                        return Guid.NewGuid();
            #pragma warning restore ELID001
                    }

                    public Guid EntityKey() => Guid.NewGuid();
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        // Only the unsuppressed site remains, so a justified v4 stays quiet while the accidental one is caught.
        var flagged = diagnostics.Where(d => d.Id == "ELID001").Should().ContainSingle().Subject;
        var line = flagged.Location.SourceTree!.GetText(TestContext.Current.CancellationToken)
            .Lines[flagged.Location.GetLineSpan().StartLinePosition.Line].ToString();
        line.Should().Contain("EntityKey");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "GuidVersionAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new GuidVersionAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
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
