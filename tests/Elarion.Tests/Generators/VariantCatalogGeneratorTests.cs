using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class VariantCatalogGeneratorTests {
    private const string Preamble =
        """
        using System;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string MailModuleDefs =
        """

        namespace Sample.Mail {
            [AppModule("Mail")] public static class MailModule { }
            public interface IEmailSender { }
            [Service]
            [ConfigurationVariant("Email:Backend", Value = "smtp", IsDefault = true)]
            public sealed class SmtpEmailSender : IEmailSender { }
            [Service]
            [ConfigurationVariant("Email:Backend", Value = "Office365")]
            public sealed class Office365EmailSender : IEmailSender { }
        }
        """;

    [Fact]
    public void EmitsRegistry_WithAccessorConsts_DescriptorData_AndGroupings() {
        var (result, diagnostics) = Run(new VariantCatalogGenerator(), Preamble + MailModuleDefs);

        diagnostics.Should().BeEmpty();
        var generated = GetGenerated(result, "ElarionVariants.g.cs");
        generated.Should().Contain("public static partial class ElarionVariants");
        generated.Should().Contain("public static class EmailBackend");
        generated.Should().Contain("public const string Key = \"Email:Backend\";");
        // Value consts are the lower-cased matched form; a named default gets a const like any other value.
        generated.Should().Contain("public const string Smtp = \"smtp\";");
        generated.Should().Contain("public const string Office365 = \"office365\";");
        generated.Should().Contain("Contract = typeof(global::Sample.Mail.IEmailSender),");
        generated.Should().Contain("DefaultValue = \"smtp\",");
        generated.Should().Contain("HasDefault = true,");
        generated.Should().Contain("Module = \"Mail\",");
        generated.Should().Contain("Axis = global::Elarion.Abstractions.Features.VariantAxis.Configuration,");
        generated.Should().Contain("public static global::System.Collections.Generic.IReadOnlyList<global::Elarion.Abstractions.Features.VariantDescriptor> All { get; }");
        generated.Should().Contain("[\"Email:Backend\"] =");
        generated.Should().Contain("[\"Mail\"] =");
    }

    [Fact]
    public void PlatformVariant_CarriesNullModule_AndAppearsInPlatformList() {
        // Adapters in an infrastructure assembly live under no module — the documented platform placement.
        var source = Preamble +
            """

            namespace Sample.Infrastructure {
                public interface IBlobCompressor { }
                [Service]
                [ConfigurationVariant("Blobs:Compression", Value = "gzip", IsDefault = true)]
                public sealed class GzipCompressor : IBlobCompressor { }
            }
            """;

        var (result, _) = Run(new VariantCatalogGenerator(), source);

        var generated = GetGenerated(result, "ElarionVariants.g.cs");
        generated.Should().Contain("Module = null,");
        generated.Should().Contain("public static global::System.Collections.Generic.IReadOnlyList<global::Elarion.Abstractions.Features.VariantDescriptor> Platform { get; } = new global::Elarion.Abstractions.Features.VariantDescriptor[] { D0 };");
    }

    [Fact]
    public void FeatureVariant_EmitsFeatureAxisDescriptor() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IForecast { }
                [Service]
                [FeatureVariant("ForecastAlgorithm")]
                public sealed class LinearForecast : IForecast { }
                [Service]
                [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
                public sealed class NeuralForecast : IForecast { }
            }
            """;

        var (result, _) = Run(new VariantCatalogGenerator(), source);

        var generated = GetGenerated(result, "ElarionVariants.g.cs");
        generated.Should().Contain("Axis = global::Elarion.Abstractions.Features.VariantAxis.Feature,");
        generated.Should().Contain("public static class ForecastAlgorithm");
        generated.Should().Contain("public const string Neural = \"neural\";");
        // The unnamed default is not a selectable value — HasDefault still reports it.
        generated.Should().Contain("HasDefault = true,");
        generated.Should().Contain("Values = new string[] { \"neural\" },");
        generated.Should().Contain("DefaultValue = null,");
    }

    [Fact]
    public void CrossAssembly_AggregatesReferencedManifestVariants_RespectingContractAccessibility() {
        static string Field(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";
        static string Entry(string ns, string key, string contract, string? value, bool isDefault, bool isPublic) =>
            Field(ns) + Field("1") + Field(key) + Field(contract) + Field(value)
            + Field(isDefault ? "1" : "0") + Field(isPublic ? "1" : "0");

        // A referenced assembly advertising one public and one internal contract's switch via its manifest.
        var producerSource =
            $$"""
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Variant.v1", "{{Entry("Sample.Platform", "Email:Backend", "global::Sample.Platform.IEmailSender", "smtp", isDefault: true, isPublic: true)}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Variant.v1", "{{Entry("Sample.Platform", "Email:Backend", "global::Sample.Platform.IEmailSender", "office365", isDefault: false, isPublic: true)}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Variant.v1", "{{Entry("Sample.Platform", "Search:Engine", "global::Sample.Platform.ISearchEngine", "lucene", isDefault: true, isPublic: false)}}")]

            namespace Sample.Platform {
                public interface IEmailSender { }
                internal interface ISearchEngine { }
            }
            """;
        var ct = TestContext.Current.CancellationToken;
        var producer = CSharpCompilation.Create(
            "Sample.Platform",
            [CSharpSyntaxTree.ParseText(producerSource, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct)],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var consumerSource = Preamble + "\n\nnamespace Host { public static class Anchor { } }";
        var consumer = CSharpCompilation.Create(
            "Host",
            [CSharpSyntaxTree.ParseText(consumerSource, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct)],
            [.. CreateMetadataReferences(), producer.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new VariantCatalogGenerator()).RunGenerators(consumer, ct);
        var generated = GetGenerated(driver.GetRunResult(), "ElarionVariants.g.cs");

        generated.Should().Contain("public const string Smtp = \"smtp\";");
        generated.Should().Contain("public const string Office365 = \"office365\";");
        generated.Should().Contain("Contract = typeof(global::Sample.Platform.IEmailSender),");
        // The internal contract's switch still enumerates — with no typeof, which would not compile here.
        generated.Should().Contain("public const string Lucene = \"lucene\";");
        generated.Should().Contain("Contract = null,");
        generated.Should().Contain("ContractName = \"Sample.Platform.ISearchEngine\",");
    }

    [Fact]
    public void ReportsElvar010_WhenSelectorsCollideOnOneAccessorName() {
        var source = Preamble +
            """

            namespace Sample.App {
                [AppModule("App")] public static class AppModule { }
                public interface IA { }
                public interface IB { }
                [Service]
                [ConfigurationVariant("Email:Backend", Value = "a", IsDefault = true)]
                public sealed class A1 : IA { }
                [Service]
                [ConfigurationVariant("email-backend", Value = "b", IsDefault = true)]
                public sealed class B1 : IB { }
            }
            """;

        var (result, diagnostics) = Run(new VariantCatalogGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR010" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
        // Both switches remain in the data surfaces; only the second typed accessor is omitted.
        var generated = GetGenerated(result, "ElarionVariants.g.cs");
        generated.Should().Contain("[\"Email:Backend\"] =");
    }

    [Fact]
    public void ManifestGenerator_EmitsVariantEntries() {
        var (result, _) = Run(new ElarionManifestGenerator(), Preamble + MailModuleDefs);

        var manifest = GetGenerated(result, "ElarionManifest.g.cs");
        manifest.Should().Contain("Elarion.Manifest.Variant.v1");
        manifest.Should().Contain("office365");
    }

    [Fact]
    public void IrrelevantEditReusesOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new VariantCatalogGenerator(), Preamble + MailModuleDefs,
            "VariantCatalogFeature", "VariantCatalogConfiguration");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(
        IIncrementalGenerator generator, string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "VariantCatalogGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        var result = driver.GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
