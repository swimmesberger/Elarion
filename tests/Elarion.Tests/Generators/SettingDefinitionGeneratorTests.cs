using System.Reflection;
using System.Runtime.Loader;
using AwesomeAssertions;
using Elarion.Abstractions.Settings;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class SettingDefinitionGeneratorTests {
    private const string Source =
        """
        using Elarion.Abstractions.Settings;

        [assembly: GenerateSettingDefinitionCatalog]

        namespace Sample.App;

        public enum Mode { Off, On }

        public sealed record Smtp(string Host, int Port);

        [SettingDefinitions]
        public static partial class AppSettings
        {
            [Setting("app:title", Default = "Untitled", Pinnable = true, Description = "The title.")]
            public static partial SettingDefinition<string> Title { get; }

            [Setting("app:retries", Default = 3)]
            public static partial SettingDefinition<int> Retries { get; }

            [Setting("app:mode", Default = Mode.On)]
            public static partial SettingDefinition<Mode> ModeSetting { get; }

            [Setting("app:smtp", DefaultFactory = nameof(DefaultSmtp), Pinnable = true)]
            public static partial SettingDefinition<Smtp> SmtpSetting { get; }

            [Setting("app:apikey", Secret = true, Scopes = ["global", "user"])]
            public static partial SettingDefinition<string> ApiKey { get; }

            private static Smtp DefaultSmtp() => new("localhost", 25);
        }
        """;

    [Fact]
    public void GeneratedDefinitionsCompileAndCarryTheDeclaredMetadata() {
        var assembly = CompileAndLoad(Source);

        var all = (IReadOnlyList<SettingDefinition>)assembly.GetType("Sample.App.AppSettings")!
            .GetProperty("All")!.GetValue(null)!;
        var catalog = (IReadOnlyList<SettingDefinition>)assembly.GetType("GeneratorTests.ElarionSettingDefinitions")!
            .GetProperty("All")!.GetValue(null)!;

        all.Select(d => d.Key).Should().BeEquivalentTo(
            ["app:apikey", "app:mode", "app:retries", "app:smtp", "app:title"]);
        catalog.Select(d => d.Key).Should().Equal(all.Select(d => d.Key));

        var title = (SettingDefinition<string>)all.Single(d => d.Key == "app:title");
        title.Default.Should().Be("Untitled");
        title.IsPinnable.Should().BeTrue();
        title.IsSecret.Should().BeFalse();
        title.Description.Should().Be("The title.");
        title.Scopes.Should().Equal("global");

        ((SettingDefinition<int>)all.Single(d => d.Key == "app:retries")).Default.Should().Be(3);
        all.Single(d => d.Key == "app:mode").HasDefault.Should().BeTrue();

        var smtp = all.Single(d => d.Key == "app:smtp");
        smtp.ValueType.Name.Should().Be("Smtp");
        smtp.HasDefault.Should().BeTrue();

        var apiKey = all.Single(d => d.Key == "app:apikey");
        apiKey.IsSecret.Should().BeTrue();
        apiKey.HasDefault.Should().BeFalse();
        apiKey.Scopes.Should().Equal("global", "user");
    }

    [Fact]
    public void OutputIsDeterministic() {
        var first = Run(Source).GeneratedTrees.Select(t => t.GetText().ToString()).OrderBy(t => t).ToArray();
        var second = Run(Source).GeneratedTrees.Select(t => t.GetText().ToString()).OrderBy(t => t).ToArray();

        second.Should().Equal(first);
    }

    [Theory]
    [InlineData("""[Setting("a:b")] public static partial SettingDefinition<string> A { get; } [Setting("A:B")] public static partial SettingDefinition<string> B { get; }""", "ELSDEF004")]
    [InlineData("""[Setting("a::b")] public static partial SettingDefinition<string> A { get; }""", "ELSDEF003")]
    [InlineData("""[Setting(" a")] public static partial SettingDefinition<string> A { get; }""", "ELSDEF003")]
    [InlineData("""[Setting("a", Secret = true, Default = "x")] public static partial SettingDefinition<string> A { get; }""", "ELSDEF005")]
    [InlineData("""[Setting("a", Pinnable = true, Scopes = ["user"])] public static partial SettingDefinition<string> A { get; }""", "ELSDEF006")]
    [InlineData("""[Setting("a", Default = "x")] public static partial SettingDefinition<int> A { get; }""", "ELSDEF007")]
    [InlineData("""[Setting("a", DefaultFactory = "Missing")] public static partial SettingDefinition<int> A { get; }""", "ELSDEF007")]
    [InlineData("""[Setting("a", Default = 1, DefaultFactory = "M")] public static partial SettingDefinition<int> A { get; } static int M() => 1;""", "ELSDEF007")]
    [InlineData("""[Setting("a")] public static SettingDefinition<string> A { get; } = null!;""", "ELSDEF002")]
    [InlineData("""[Setting("a")] public static partial string A { get; }""", "ELSDEF002")]
    public void InvalidDeclarations_ReportDiagnostics(string members, string id) {
        var result = Run($$"""
            using Elarion.Abstractions.Settings;
            namespace T;
            [SettingDefinitions] public static partial class S { {{members}} }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == id && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NonStaticOrNonPartialContainer_ReportsElsdef001() {
        var result = Run(
            """
            using Elarion.Abstractions.Settings;
            namespace T;
            [SettingDefinitions] public class S { }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELSDEF001");
    }

    [Fact]
    public void WithoutTheCatalogTrigger_NoAggregateIsEmitted() {
        var result = Run(
            """
            using Elarion.Abstractions.Settings;
            namespace T;
            [SettingDefinitions] public static partial class S {
                [Setting("a")] public static partial SettingDefinition<string> A { get; }
            }
            """);

        result.GeneratedTrees.Select(t => Path.GetFileName(t.FilePath)).Should()
            .NotContain(f => f.StartsWith("ElarionSettingDefinitions"));
    }

    [Fact]
    public void ManifestPublishesTheContainerForCrossAssemblyAggregation() {
        var compilation = Compile(Source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new SettingDefinitionGenerator().AsSourceGenerator(), new ElarionManifestGenerator().AsSourceGenerator());
        var text = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult().GeneratedTrees
            .Single(t => Path.GetFileName(t.FilePath) == "ElarionManifest.g.cs").GetText(TestContext.Current.CancellationToken).ToString();

        text.Should().Contain("Elarion.Manifest.SettingContainer.v1").And.Contain("Sample.App.AppSettings");
    }

    [Fact]
    public void IrrelevantEditReusesOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new SettingDefinitionGenerator(), Source, "SettingContainers", "SettingContainersCollected");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new SettingDefinitionGenerator(), Source, "SettingContainers");
    }

    [Fact]
    public void ManifestGeneratorStaysCachedForSettingContainers() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionManifestGenerator(), Source, "ManifestSettingContainers");
    }

    private static CSharpCompilation Compile(string source) {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        return CSharpCompilation.Create(
            "GeneratorTests",
            [CSharpSyntaxTree.ParseText(source, parse)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorDriverRunResult Run(string source) {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SettingDefinitionGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
        return driver.RunGenerators(Compile(source), TestContext.Current.CancellationToken).GetRunResult();
    }

    private static Assembly CompileAndLoad(string source) {
        var ct = TestContext.Current.CancellationToken;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SettingDefinitionGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
        driver.RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out var generatorDiagnostics, ct);

        generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        output.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        using var stream = new MemoryStream();
        output.Emit(stream, cancellationToken: ct).Success.Should().BeTrue();
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static IReadOnlyList<MetadataReference> References() {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        tpa.Should().NotBeNull();
        return tpa!.Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
