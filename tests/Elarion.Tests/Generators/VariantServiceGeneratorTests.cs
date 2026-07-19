using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class VariantServiceGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Features;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string AlgorithmDefs =
        """

        namespace Sample.App {
            [AppModule("App")] public static class AppModule { }
            public interface IAlgorithm { }
            [Service]
            [FeatureVariant("ForecastAlgorithm")]
            public sealed class LinearAlgo : IAlgorithm { }
            [Service]
            [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
            public sealed class NeuralAlgo : IAlgorithm { }
        }
        """;

    [Fact]
    public void HandlerInjectingVariantContract_IsWrappedInAsyncResolvedHandler() {
        var source = Preamble + AlgorithmDefs +
                     """

                     namespace Sample.App {
                         public sealed record ForecastCommand(int Id) : ICommand;
                         public sealed record ForecastResponse(string Name);

                         public sealed class RunForecast(IAlgorithm algorithm)
                             : IHandler<ForecastCommand, Result<ForecastResponse>> {
                             public ValueTask<Result<ForecastResponse>> HandleAsync(ForecastCommand request, CancellationToken ct) =>
                                 ValueTask.FromResult(Result<ForecastResponse>.Success(new ForecastResponse("x")));
                         }

                         public sealed record PlainQuery(int Id) : IQuery;
                         public sealed class PlainHandler : IHandler<PlainQuery, Result<ForecastResponse>> {
                             public ValueTask<Result<ForecastResponse>> HandleAsync(PlainQuery request, CancellationToken ct) =>
                                 ValueTask.FromResult(Result<ForecastResponse>.Success(new ForecastResponse("x")));
                         }
                     }
                     """;

        var (result, _) = Run(new HandlerRegistrationGenerator(), source);

        var gated = GetGenerated(result, "Sample_App_RunForecast.g.cs");
        gated.Should().Contain("global::Elarion.Abstractions.Pipeline.AsyncResolvedHandler<");
        gated.Should().Contain("WarmAsync<global::Sample.App.IAlgorithm>");

        // A handler with no variant dependency keeps the synchronous registration.
        GetGenerated(result, "Sample_App_PlainHandler.g.cs").Should().NotContain("AsyncResolvedHandler");
    }

    [Fact]
    public void HandlerPinningVariantContractWithFromKeyedServices_KeepsSynchronousRegistration() {
        // [FromKeyedServices] pins a specific keyed implementation — DI resolves it directly, bypassing
        // variant selection — so the handler must not pay the proxy (nor warm a switch it never consults).
        var source = Preamble + AlgorithmDefs +
                     """

                     namespace Sample.App {
                         using Microsoft.Extensions.DependencyInjection;

                         public sealed record PinnedCommand(int Id) : ICommand;

                         public sealed class PinnedForecast([FromKeyedServices("neural")] IAlgorithm algorithm)
                             : IHandler<PinnedCommand, Result<string>> {
                             public ValueTask<Result<string>> HandleAsync(PinnedCommand request, CancellationToken ct) =>
                                 ValueTask.FromResult(Result<string>.Success("x"));
                         }
                     }
                     """;

        var (result, _) = Run(new HandlerRegistrationGenerator(), source);

        GetGenerated(result, "Sample_App_PinnedForecast.g.cs").Should().NotContain("AsyncResolvedHandler");
    }

    [Fact]
    public void HandlerMixingPinnedAndSelectedVariantContract_IsStillWrapped() {
        // The unkeyed parameter still needs the warmed per-scope selection; only the pinned one opts out.
        var source = Preamble + AlgorithmDefs +
                     """

                     namespace Sample.App {
                         using Microsoft.Extensions.DependencyInjection;

                         public sealed record CompareCommand(int Id) : ICommand;

                         public sealed class CompareForecasts(
                             IAlgorithm selected,
                             [FromKeyedServices("neural")] IAlgorithm pinned)
                             : IHandler<CompareCommand, Result<string>> {
                             public ValueTask<Result<string>> HandleAsync(CompareCommand request, CancellationToken ct) =>
                                 ValueTask.FromResult(Result<string>.Success("x"));
                         }
                     }
                     """;

        var (result, _) = Run(new HandlerRegistrationGenerator(), source);

        var generated = GetGenerated(result, "Sample_App_CompareForecasts.g.cs");
        generated.Should().Contain("AsyncResolvedHandler");
        generated.Should().Contain("WarmAsync<global::Sample.App.IAlgorithm>");
    }

    [Fact]
    public void EmitsKeyedRegistrations_Binding_And_TransparentFactory() {
        var (result, _) = Run(new VariantServiceRegistrationGenerator(), Preamble + AlgorithmDefs);

        var generated = AllGenerated(result);
        generated.Should().Contain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
        generated.Should().Contain("\"neural\"");
        generated.Should().Contain("global::Elarion.Abstractions.Features.VariantServiceKeys.Default");
        generated.Should().Contain("typeof(global::Sample.App.NeuralAlgo)");
    }

    [Fact]
    public void MultiInterfaceVariant_AppliesToEachContract() {
        // The contract(s) come from [Service]; a variant impl that registers under several interfaces is
        // variant-resolved on each of them — the same rule [Service] uses, applied to all.
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         public interface IWarmup { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm")]
                         public sealed class LinearAlgo : IAlgorithm, IWarmup { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
                         public sealed class NeuralAlgo : IAlgorithm, IWarmup { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id.StartsWith("ELVAR") && d.Severity == DiagnosticSeverity.Error).Should().BeFalse();
        var generated = AllGenerated(result);
        generated.Should().Contain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
        generated.Should().Contain("AddElarionVariantService<global::Sample.App.IWarmup>");
        generated.Should().Contain("typeof(global::Sample.App.NeuralAlgo)");
    }

    [Fact]
    public void ReportsElvar007_WhenVariantImplementationIsNotAService() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [FeatureVariant("ForecastAlgorithm")]
                         public sealed class LinearAlgo : IAlgorithm { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR007" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void ServiceGenerator_SkipsPlainRegistration_ForFeatureVariantClass() {
        // [FeatureVariant] is a modifier on [Service]: the service generator must NOT also emit a plain (unkeyed)
        // registration, or it would collide with the variant generator's transparent contract registration.
        var (result, _) = Run(new ModuleServiceRegistrationGenerator(), Preamble + AlgorithmDefs);

        var generated = AllGenerated(result);
        generated.Should().NotContain("LinearAlgoServiceRegistration");
        generated.Should().NotContain("NeuralAlgoServiceRegistration");
    }

    [Fact]
    public void ReportsElvar003_WhenNoDefaultImplementation() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
                         public sealed class NeuralAlgo : IAlgorithm { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR003" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar001_WhenDuplicateVariantKey() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
                         public sealed class NeuralA : IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm", Variant = "neural")]
                         public sealed class NeuralB : IAlgorithm { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        // A reported error gates emission: no arbitrary "first-alphabetical wins" registration is shipped.
        AllGenerated(result).Should().NotContain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
    }

    [Fact]
    public void ReportsElvar004_AndSuppressesEmission_WhenContractBoundToTwoFeatures() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [Service]
                         [FeatureVariant("FeatureA")]
                         public sealed class DefaultAlgo : IAlgorithm { }
                         [Service]
                         [FeatureVariant("FeatureB", Variant = "neural")]
                         public sealed class NeuralAlgo : IAlgorithm { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR004" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        // The conflicting contract is not emitted with an arbitrarily-picked feature.
        AllGenerated(result).Should().NotContain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
    }

    private const string EmailDefs =
        """

        namespace Sample.Mail {
            [AppModule("Mail")] public static class MailModule { }
            public interface IEmailSender { }
            [Service]
            [ConfigurationVariant("Email:Backend")]
            public sealed class SmtpEmailSender : IEmailSender { }
            [Service]
            [ConfigurationVariant("Email:Backend", Value = "Office365")]
            public sealed class Office365EmailSender : IEmailSender { }
        }
        """;

    [Fact]
    public void ConfigurationVariant_EmitsConfigurationRegistration_WithLowercasedKeys() {
        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), Preamble + EmailDefs);

        diagnostics.Any(d => d.Id.StartsWith("ELVAR")).Should().BeFalse();
        var generated = AllGenerated(result);
        generated.Should().Contain("AddElarionConfigurationVariantService<global::Sample.Mail.IEmailSender>");
        generated.Should().Contain("\"Email:Backend\"");
        // The declared Value is lower-cased at emit time so the runtime's lowered configured value matches it.
        generated.Should().Contain("\"office365\"");
        generated.Should().NotContain("\"Office365\"");
        generated.Should().Contain("typeof(global::Sample.Mail.Office365EmailSender)");
        generated.Should().Contain("global::Elarion.Abstractions.Features.VariantServiceKeys.Default");
        // The configuration axis never registers through the feature-selected API.
        generated.Should().NotContain("AddElarionVariantService<global::Sample.Mail.IEmailSender>");
    }

    [Fact]
    public void HandlerInjectingConfigurationVariantContract_KeepsSynchronousRegistration() {
        // Configuration-selected variants resolve synchronously (a configuration read), so the handler must not
        // pay the async-resolving proxy the feature axis needs.
        var source = Preamble + EmailDefs +
                     """

                     namespace Sample.Mail {
                         public sealed record SendCommand(int Id) : ICommand;

                         public sealed class SendEmail(IEmailSender sender) : IHandler<SendCommand, Result<string>> {
                             public ValueTask<Result<string>> HandleAsync(SendCommand request, CancellationToken ct) =>
                                 ValueTask.FromResult(Result<string>.Success("sent"));
                         }
                     }
                     """;

        var (result, _) = Run(new HandlerRegistrationGenerator(), source);

        GetGenerated(result, "Sample_Mail_SendEmail.g.cs").Should().NotContain("AsyncResolvedHandler");
    }

    [Fact]
    public void ServiceGenerator_SkipsPlainRegistration_ForConfigurationVariantClass() {
        var (result, _) = Run(new ModuleServiceRegistrationGenerator(), Preamble + EmailDefs);

        var generated = AllGenerated(result);
        generated.Should().NotContain("SmtpEmailSenderServiceRegistration");
        generated.Should().NotContain("Office365EmailSenderServiceRegistration");
    }

    [Fact]
    public void ReportsElvar008_AndSuppressesEmission_WhenContractMixesSelectionAxes() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm")]
                         public sealed class LinearAlgo : IAlgorithm { }
                         [Service]
                         [ConfigurationVariant("Forecast:Algorithm", Value = "neural")]
                         public sealed class NeuralAlgo : IAlgorithm { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR008" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        // The mixed contract is not emitted with an arbitrarily-picked axis.
        AllGenerated(result).Should().NotContain("VariantService<global::Sample.App.IAlgorithm>");
    }

    [Fact]
    public void ReportsElvar009_WhenConfigurationKeyIsBlank() {
        var source = Preamble +
                     """

                     namespace Sample.Mail {
                         [AppModule("Mail")] public static class MailModule { }
                         public interface IEmailSender { }
                         [Service]
                         [ConfigurationVariant("  ")]
                         public sealed class SmtpEmailSender : IEmailSender { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR009" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar001_WhenConfigurationValuesDifferOnlyByCase() {
        // Configuration values match case-insensitively, so two variants distinguished only by case collide.
        var source = Preamble +
                     """

                     namespace Sample.Mail {
                         [AppModule("Mail")] public static class MailModule { }
                         public interface IEmailSender { }
                         [Service]
                         [ConfigurationVariant("Email:Backend", Value = "office365")]
                         public sealed class GraphEmailSender : IEmailSender { }
                         [Service]
                         [ConfigurationVariant("Email:Backend", Value = "OFFICE365")]
                         public sealed class LegacyEmailSender : IEmailSender { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar003_WhenNoDefaultConfigurationVariant() {
        var source = Preamble +
                     """

                     namespace Sample.Mail {
                         [AppModule("Mail")] public static class MailModule { }
                         public interface IEmailSender { }
                         [Service]
                         [ConfigurationVariant("Email:Backend", Value = "office365")]
                         public sealed class Office365EmailSender : IEmailSender { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR003" && d.Severity == DiagnosticSeverity.Warning).Should().BeTrue();
    }

    [Fact]
    public void ReportsElvar007_WhenConfigurationVariantIsNotAService() {
        var source = Preamble +
                     """

                     namespace Sample.Mail {
                         [AppModule("Mail")] public static class MailModule { }
                         public interface IEmailSender { }
                         [ConfigurationVariant("Email:Backend")]
                         public sealed class SmtpEmailSender : IEmailSender { }
                     }
                     """;

        var (_, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR007" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
    }

    [Fact]
    public void NamedDefault_RegistersUnderItsValue_AndBecomesTheDefaultKey() {
        var source = Preamble +
                     """

                     namespace Sample.Mail {
                         [AppModule("Mail")] public static class MailModule { }
                         public interface IEmailSender { }
                         [Service]
                         [ConfigurationVariant("Email:Backend", Value = "smtp", IsDefault = true)]
                         public sealed class SmtpEmailSender : IEmailSender { }
                         [Service]
                         [ConfigurationVariant("Email:Backend", Value = "office365")]
                         public sealed class Office365EmailSender : IEmailSender { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id.StartsWith("ELVAR")).Should().BeFalse();
        var generated = AllGenerated(result);
        generated.Should().Contain(
            "AddElarionConfigurationVariantService<global::Sample.Mail.IEmailSender>(services, \"Email:Backend\", \"smtp\")");
        // A named default is keyed under its own value; the collision-proof sentinel never appears.
        generated.Should().NotContain("VariantServiceKeys.Default");
    }

    [Fact]
    public void ReportsElvar001_WhenTwoDefaultsAreDeclared() {
        var source = Preamble +
                     """

                     namespace Sample.App {
                         [AppModule("App")] public static class AppModule { }
                         public interface IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm")]
                         public sealed class LinearAlgo : IAlgorithm { }
                         [Service]
                         [FeatureVariant("ForecastAlgorithm", Variant = "neural", IsDefault = true)]
                         public sealed class NeuralAlgo : IAlgorithm { }
                     }
                     """;

        var (result, diagnostics) = Run(new VariantServiceRegistrationGenerator(), source);

        diagnostics.Any(d => d.Id == "ELVAR001" && d.Severity == DiagnosticSeverity.Error).Should().BeTrue();
        AllGenerated(result).Should().NotContain("AddElarionVariantService<global::Sample.App.IAlgorithm>");
    }

    [Fact]
    public void ModuleAggregation_IncludesConfigurationVariantContracts() {
        var (result, _) = Run(new VariantServiceRegistrationGenerator(), Preamble + EmailDefs);

        var aggregation = GetGenerated(result, "MailVariantServiceExtensions.g.cs");
        aggregation.Should().Contain("AddMailVariantServices");
        aggregation.Should().Contain("VariantService(services);");
    }

    [Fact]
    public void IrrelevantEditReusesVariantPipeline() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new VariantServiceRegistrationGenerator(), Preamble + AlgorithmDefs + EmailDefs,
            "VariantServices", "ConfigurationVariantServices");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(
        IIncrementalGenerator generator, string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "VariantServiceGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        var result = driver.GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static string AllGenerated(GeneratorDriverRunResult result) {
        return string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
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
