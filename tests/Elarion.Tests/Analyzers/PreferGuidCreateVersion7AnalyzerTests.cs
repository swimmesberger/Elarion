using System.Collections.Immutable;
using System.Threading.Tasks;
using AwesomeAssertions;
using Elarion.Generators;
using Elarion.Generators.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Elarion.Tests.Analyzers;

public sealed class PreferGuidCreateVersion7AnalyzerTests
{
    [Fact]
    public async Task NewGuidInvocation_IsReported()
    {
        const string source =
            """
            public static class IdFactory {
                public static System.Guid Next() => System.Guid.NewGuid();
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        var diagnostic = diagnostics.Should().ContainSingle(d => d.Id == "ELID001").Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Contain("Guid.CreateVersion7()");
    }

    [Fact]
    public async Task EveryInvocationForm_IsReported()
    {
        // Flagging every NewGuid() (not just "entity id" positions) is deliberate — including the
        // using-static form, which has no receiver expression.
        const string source =
            """
            using System;
            using static System.Guid;

            public static class IdFactory {
                public static Guid A() => Guid.NewGuid();
                public static Guid B() => System.Guid.NewGuid();
                public static Guid C() => NewGuid();
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Count(d => d.Id == "ELID001").Should().Be(3);
    }

    [Fact]
    public async Task CreateVersion7Invocation_IsNotReported()
    {
        const string source =
            """
            public static class IdFactory {
                public static System.Guid Next() => System.Guid.CreateVersion7();
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELID001");
    }

    [Fact]
    public async Task NewGuidOnAForeignGuidType_IsNotReported()
    {
        const string source =
            """
            namespace Custom {
                public readonly struct Guid {
                    public static Guid NewGuid() => default;
                }

                public static class IdFactory {
                    public static Guid Next() => Guid.NewGuid();
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "ELID001");
    }

    [Fact]
    public async Task CodeFix_RewritesTheMethodName_PreservingTheReceiverSpelling()
    {
        const string source =
            """
            using System;

            public static class IdFactory {
                public static Guid Short() => Guid.NewGuid();
                public static Guid Qualified() => System.Guid.NewGuid();
            }
            """;

        var fixedSource = await ApplyFixAsync(source, occurrenceIndex: 0);
        fixedSource.Should().Contain("Guid.CreateVersion7()");
        fixedSource.Should().Contain("System.Guid.NewGuid()");

        fixedSource = await ApplyFixAsync(source, occurrenceIndex: 1);
        fixedSource.Should().Contain("System.Guid.CreateVersion7()");
        fixedSource.Should().Contain("=> Guid.NewGuid()");
    }

    [Fact]
    public async Task CodeFix_RewritesTheUsingStaticForm()
    {
        const string source =
            """
            using System;
            using static System.Guid;

            public static class IdFactory {
                public static Guid Next() => NewGuid();
            }
            """;

        var fixedSource = await ApplyFixAsync(source, occurrenceIndex: 0);

        fixedSource.Should().Contain("=> CreateVersion7()");
        fixedSource.Should().NotContain("NewGuid");
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var compilation = CreateCompilation(source);

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new PreferGuidCreateVersion7Analyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static async Task<string> ApplyFixAsync(string source, int occurrenceIndex)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace
            .AddProject("PreferGuidCreateVersion7AnalyzerTests", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))
            .WithMetadataReferences(CreateMetadataReferences());
        var document = project.AddDocument("Test.cs", SourceText.From(source));

        var compilation = await document.Project.GetCompilationAsync();
        var diagnostics = await compilation!
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PreferGuidCreateVersion7Analyzer()))
            .GetAnalyzerDiagnosticsAsync();
        var diagnostic = diagnostics
            .Where(d => d.Id == "ELID001")
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ElementAt(occurrenceIndex);

        CodeAction? registered = null;
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => registered ??= action,
            TestContext.Current.CancellationToken);
        await new PreferGuidCreateVersion7CodeFixProvider().RegisterCodeFixesAsync(context);
        registered.Should().NotBeNull();

        var operations = await registered!.GetOperationsAsync(TestContext.Current.CancellationToken);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var changedDocument = changedSolution.GetDocument(document.Id);
        return (await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "PreferGuidCreateVersion7AnalyzerTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return compilation;
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
