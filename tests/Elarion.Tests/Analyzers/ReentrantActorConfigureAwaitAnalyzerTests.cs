using System.Collections.Immutable;
using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Elarion.Tests.Analyzers;

public sealed class ReentrantActorConfigureAwaitAnalyzerTests {
    [Fact]
    public async Task ConfigureAwaitFalse_InReentrantActorMethod_IsReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Actors.Reentrant]
                public sealed class SessionActor {
                    public async System.Threading.Tasks.Task Touch() {
                        await System.Threading.Tasks.Task.Yield();
                        await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELACT006" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ConfigureAwaitFalse_InNonReentrantActor_IsNotReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                public sealed class SessionActor {
                    public async System.Threading.Tasks.Task Touch() =>
                        await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELACT006");
    }

    [Fact]
    public async Task ConfigureAwaitTrue_InReentrantActor_IsNotReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Actors.Reentrant]
                public sealed class SessionActor {
                    public async System.Threading.Tasks.Task Touch() =>
                        await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(true);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELACT006");
    }

    [Fact]
    public async Task ConfigureAwaitFalse_InLambdaInsideReentrantActor_IsReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Actors.Reentrant]
                public sealed class SessionActor {
                    public System.Threading.Tasks.Task Touch() {
                        var work = async () => {
                            await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);
                        };
                        return work();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELACT006");
    }

    [Fact]
    public async Task ConfigureAwaitOptionsWithoutCapture_InReentrantActor_IsReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Actors.Reentrant]
                public sealed class SessionActor {
                    public async System.Threading.Tasks.Task Touch() =>
                        await System.Threading.Tasks.Task.Delay(1)
                            .ConfigureAwait(System.Threading.Tasks.ConfigureAwaitOptions.None);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "ELACT006");
    }

    [Fact]
    public async Task ConfigureAwaitOptionsWithCapture_InReentrantActor_IsNotReported() {
        const string source =
            """
            namespace App.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Actors.Reentrant]
                public sealed class SessionActor {
                    public async System.Threading.Tasks.Task Touch() =>
                        await System.Threading.Tasks.Task.Delay(1)
                            .ConfigureAwait(System.Threading.Tasks.ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELACT006");
    }

    [Fact]
    public async Task ConfigureAwaitFalse_OutsideActors_IsNotReported() {
        const string source =
            """
            namespace App.Orders {
                public sealed class PlainService {
                    public async System.Threading.Tasks.Task Touch() =>
                        await System.Threading.Tasks.Task.Delay(1).ConfigureAwait(false);
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELACT006");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ReentrantActorConfigureAwaitAnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ReentrantActorConfigureAwaitAnalyzer()));
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
