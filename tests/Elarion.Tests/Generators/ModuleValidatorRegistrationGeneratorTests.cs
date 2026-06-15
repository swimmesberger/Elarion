using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ModuleValidatorRegistrationGeneratorTests
{
    [Fact]
    public void GenerateValidators_UseElarion_EmitsModuleMethod()
    {
        const string source =
            """
            [assembly: Elarion.Abstractions.UseElarion]

            namespace Elarion.Abstractions {
                [System.AttributeUsage(System.AttributeTargets.Assembly)]
                public sealed class GenerateModuleValidatorsAttribute : System.Attribute;

                [System.AttributeUsage(System.AttributeTargets.Assembly)]
                public sealed class UseElarionAttribute : System.Attribute;
            }

            namespace Elarion.Abstractions.Modules {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute(string name) : System.Attribute {
                    public string Name { get; } = name;
                }
            }

            namespace FluentValidation {
                public interface IValidator<in T>;

                public abstract class AbstractValidator<T> : IValidator<T>;
            }

            namespace Sample.Modules.Billing {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static partial class BillingModule {
                }

                public sealed record CreateInvoiceCommand;

                public sealed class CreateInvoiceValidator : FluentValidation.AbstractValidator<CreateInvoiceCommand> {
                }
            }
            """;

        var result = Generate(source);
        var generated = GetGeneratedSource(result, "BillingValidatorExtensions.g.cs");

        generated.Should().Contain("public static IServiceCollection AddBillingValidators(");
        generated.Should().Contain(
            "services.AddScoped<IValidator<global::Sample.Modules.Billing.CreateInvoiceCommand>, global::Sample.Modules.Billing.CreateInvoiceValidator>();");
    }

    private static GeneratorDriverRunResult Generate(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "ModuleValidatorRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ModuleValidatorRegistrationGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult();

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return result;
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

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
