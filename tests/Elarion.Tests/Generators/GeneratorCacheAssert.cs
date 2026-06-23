using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Tests.Generators;

/// <summary>
/// Asserts that an incremental generator's tracked pipeline nodes are reused after an edit that
/// re-runs the discovery transform but produces an equal model — i.e. the pipeline value models are
/// genuinely value-equatable and the generator does not re-emit on unrelated edits.
/// </summary>
internal static class GeneratorCacheAssert
{
    /// <summary>
    /// Runs <paramref name="generator"/> over <paramref name="source"/>, then appends an unrelated
    /// declaration to the same syntax tree (forcing the transform to re-run) and asserts every output
    /// of each named tracked step reports <see cref="IncrementalStepRunReason.Unchanged"/> or
    /// <see cref="IncrementalStepRunReason.Cached"/> on the second run.
    /// </summary>
    public static void ReusesOutputsAfterIrrelevantEdit(
        IIncrementalGenerator generator,
        string source,
        params string[] trackingNames)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "GeneratorCacheTests",
            [tree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            parseOptions: parseOptions,
            additionalTexts: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        // Re-parse the same tree with an unrelated declaration appended. This invalidates the tree's
        // semantic model so the discovery transform re-runs, but the discovered model is unchanged —
        // so a value-equatable pipeline keeps every downstream node cached.
        var editedTree = CSharpSyntaxTree.ParseText(
            source + "\n\nnamespace __CacheProbe { internal sealed class __Probe { } }",
            parseOptions);
        var editedCompilation = compilation.ReplaceSyntaxTree(tree, editedTree);
        driver = driver.RunGenerators(editedCompilation);

        var result = driver.GetRunResult().Results.Single();
        foreach (var trackingName in trackingNames)
        {
            result.TrackedSteps.Should().ContainKey(
                trackingName,
                "the generator must tag its '{0}' node with WithTrackingName", trackingName);

            var reasons = result.TrackedSteps[trackingName]
                .SelectMany(step => step.Outputs)
                .Select(output => output.Reason)
                .ToArray();

            reasons.Should().NotBeEmpty();
            reasons.Should().OnlyContain(
                reason => reason == IncrementalStepRunReason.Unchanged || reason == IncrementalStepRunReason.Cached,
                "an edit that does not change the discovered model must not re-run the '{0}' step", trackingName);
        }
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
