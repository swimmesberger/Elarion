using System.Reflection;
using AwesomeAssertions;
using Elarion.Generators;
using Elarion.Spreadsheets;
using Elarion.Spreadsheets.Generators;
using Elarion.Validation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Elarion.Tests.Spreadsheets.HandBuiltWorkbook;

namespace Elarion.Tests.Spreadsheets;

/// <summary>
/// Validation at read time (FR-047, FR-048), end to end the way an application wires it: a spreadsheet record with
/// DataAnnotations inside a module of an assembly marked for generation is compiled by the real generators — the record
/// mapper, the module's validation resolver and its <c>ConfigureDefaultServices</c> — and read through the registered
/// <see cref="SpreadsheetReader"/>.
/// </summary>
public sealed class SpreadsheetValidationTests {
    private const string Source =
        """
        using System.ComponentModel.DataAnnotations;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;
        using Elarion.Spreadsheets;

        [assembly: GenerateModuleHandlers]

        namespace Sample.Crm {
            [AppModule("Crm")]
            public static class CrmModule { }

            [SpreadsheetRecord("Contacts")]
            public sealed partial class ContactImportRow {
                [StringLength(20, MinimumLength = 2)]
                public required string Name { get; init; }

                public string? Phone { get; init; }

                [SpreadsheetColumn("E-mail")]
                [EmailAddress(ErrorMessage = "Keine gültige E-Mail-Adresse.")]
                public string? Email { get; init; }
            }
        }
        """;

    private static readonly Lazy<Assembly> Compiled = new(CompileAndLoad);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Type RowType => Compiled.Value.GetType("Sample.Crm.ContactImportRow")!;

    [Fact]
    public async Task InvalidEmailInRow13_IsOneErrorNamingTheColumnTheCellAndTheValidatorsMessage() {
        var rows = TextRow(1, "Name", "Phone", "E-mail");
        for (var r = 2; r <= 14; r++) rows += TextRow(r, $"Person {r}", "", r == 13 ? "not-an-address" : $"p{r}@example.com");
        await using var provider = BuildProvider(enableModule: true);
        using var scope = provider.CreateScope();

        var result = await ReadAsync(scope.ServiceProvider.GetRequiredService<SpreadsheetReader>(), Build(rows));

        var errors = ((IEnumerable<SpreadsheetError>)result.GetType().GetProperty("Errors")!.GetValue(result)!).ToList();
        var error = errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(SpreadsheetErrorCodes.Validation);
        error.RowNumber.Should().Be(13);
        error.Header.Should().Be("E-mail");
        error.CellReference.Should().Be("C13");
        error.Message.Should().Be("Keine gültige E-Mail-Adresse.");
    }

    [Fact]
    public async Task ValidRowReadsWithoutError_AndARuleViolationIsLocatedAtItsCell() {
        await using var provider = BuildProvider(enableModule: true);
        using var scope = provider.CreateScope();
        var bytes = Build(TextRow(1, "E-mail", "Name") + TextRow(2, "a@example.com", "Anna") + TextRow(3, "", "X"));

        var result = await ReadAsync(scope.ServiceProvider.GetRequiredService<SpreadsheetReader>(), bytes);

        var errors = ((IEnumerable<SpreadsheetError>)result.GetType().GetProperty("Errors")!.GetValue(result)!).ToList();
        errors.Should().ContainSingle().Which.CellReference.Should().Be("B3");
    }

    [Fact]
    public async Task RecordWithValidationAttributes_WithoutAddElarionValidation_FailsNamingTheCause() {
        var services = new ServiceCollection();
        services.AddElarionSpreadsheets();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var read = () => ReadAsync(scope.ServiceProvider.GetRequiredService<SpreadsheetReader>(), Build(TextRow(1, "Name")));

        (await read.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Contain("Sample.Crm.ContactImportRow").And.Contain("AddElarionValidation()");
    }

    [Fact]
    public async Task RecordWithValidationAttributes_WhoseModuleIsDisabled_FailsNamingTheCause() {
        await using var provider = BuildProvider(enableModule: false);
        using var scope = provider.CreateScope();

        var read = () => ReadAsync(scope.ServiceProvider.GetRequiredService<SpreadsheetReader>(), Build(TextRow(1, "Name")));

        (await read.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("module is disabled");
    }

    [Fact]
    public void GeneratedMapper_SaysTheRecordCarriesValidation() {
        var mapper = RowType.GetProperty("SpreadsheetMapper", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        mapper.GetType().GetProperty("HasValidation")!.GetValue(mapper).Should().Be(true);
    }

    private static ServiceProvider BuildProvider(bool enableModule) {
        var services = new ServiceCollection();
        services.AddElarionValidation();
        services.AddElarionSpreadsheets();
        if (enableModule) {
            // A disabled module's ConfigureDefaultServices never runs, so its resolver is never registered.
            Compiled.Value.GetType("Sample.Crm.CrmModuleElarionModuleServices")!
                .GetMethod("ConfigureDefaultServices", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [services]);
        }

        return services.BuildServiceProvider();
    }

    private static async Task<object> ReadAsync(SpreadsheetReader reader, byte[] bytes) {
        var method = typeof(SpreadsheetReader).GetMethod(nameof(SpreadsheetReader.ReadAsync))!.MakeGenericMethod(RowType);
        var task = method.Invoke(reader, [new MemoryStream(bytes), null, null, CancellationToken])!;
        var asTask = (Task)task.GetType().GetMethod("AsTask")!.Invoke(task, null)!;
        await asTask;
        return asTask.GetType().GetProperty("Result")!.GetValue(asTask)!;
    }

    /// <summary>Runs the record, validation and module generators over <see cref="Source"/> and loads the assembly.</summary>
    private static Assembly CompileAndLoad() {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "Elarion.Tests.SpreadsheetValidation.Sample",
            [CSharpSyntaxTree.ParseText(Source, parseOptions)],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)SharedMetadataReferences.FromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
                new SpreadsheetRecordGenerator().AsSourceGenerator(),
                new ValidationResolverGenerator().AsSourceGenerator(),
                new ModuleDefaultServicesGenerator().AsSourceGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return Assembly.Load(stream.ToArray());
    }
}
