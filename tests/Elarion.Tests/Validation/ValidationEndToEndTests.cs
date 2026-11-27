using System.Reflection;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Results;
using Elarion.Abstractions.Validation;
using Elarion.Generators;
using Elarion.Validation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Elarion.Pipeline;

namespace Elarion.Tests.Validation;

/// <summary>
/// The whole ADR-0027 chain, end to end, the way the Billing sample wires it: DataAnnotations on a handler
/// request DTO are compiled by the real generators (the per-module <c>IValidatableInfoResolver</c>, the
/// module <c>ConfigureDefaultServices</c> skeleton, and the auto-attached framework
/// <c>ValidationDecorator</c>) into a loadable assembly; <c>AddElarionValidation()</c> plus the generated
/// <c>ConfigureDefaultServices</c> compose the container; and the resolved handler pipeline fails an invalid
/// request with <see cref="ErrorKind.Validation"/> and wire-named (camelCase) field keys while a valid
/// request reaches the handler.
/// </summary>
public sealed class ValidationEndToEndTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Source =
        """
        using System.ComponentModel.DataAnnotations;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Results;
        using Elarion.Abstractions.Validation;

        [assembly: GenerateModuleHandlers]

        namespace Sample.App {
            [AppModule("App")]
            public static class AppModule { }

            public sealed record CreateClientCommand : ICommand {
                [StringLength(200, MinimumLength = 3)]
                public required string Name { get; init; }

                [EmailAddress]
                public required string Email { get; init; }
            }

            public sealed class CreateClientHandler : IHandler<CreateClientCommand, Result<Unit>> {
                public ValueTask<Result<Unit>> HandleAsync(CreateClientCommand request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<Unit>.Success(default));
            }

            public sealed record ExportClientsRequest {
                [Range(1, 100)] public int Page { get; init; }
            }

            // Validated outside a handler request: no handler takes it, the marker makes it a root of the module.
            [ValidationRoot]
            public sealed record ClientImportRow {
                [StringLength(10, MinimumLength = 2)]
                public required string Code { get; init; }
            }

            [AllowAnonymous]
            // Rows of a request list: each item is walked at its indexed path (rows[0].code).
            public sealed record ImportHandoversCommand : ICommand {
                public required IReadOnlyList<HandoverRow> Rows { get; init; }
            }

            public sealed record HandoverRow([StringLength(10, MinimumLength = 2)] string Code) {
                [Range(1, 99)] public int Count { get; init; } = 1;
            }

            public sealed class ImportHandoversHandler : IHandler<ImportHandoversCommand, Result<Unit>> {
                public ValueTask<Result<Unit>> HandleAsync(ImportHandoversCommand request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<Unit>.Success(default));
            }

            // A single-value rule on a nullable member applies to a present value only.
            public sealed record StatsQuery : ICommand {
                [AllowedValues(30, 90, 365)] public int? Days { get; init; }
                [AllowedValues("week", "month")] public string? Span { get; init; }
                [AllowedValues(null, 1, 2)] public int? Level { get; init; }
            }

            public sealed class StatsHandler : IHandler<StatsQuery, Result<Unit>> {
                public ValueTask<Result<Unit>> HandleAsync(StatsQuery request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<Unit>.Success(default));
            }

            [AllowAnonymous]
            public sealed class ExportClientsHandler : IStreamHandler<ExportClientsRequest, string> {
                public ValueTask<Result<IAsyncEnumerable<string>>> HandleAsync(ExportClientsRequest request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<IAsyncEnumerable<string>>.Success(Values()));
                private static async IAsyncEnumerable<string> Values() { yield return "ok"; await Task.Yield(); }
            }
        }
        """;

    [Fact]
    public async Task AnnotatedRequest_FailsThroughGeneratedPipelineWithFieldKeys_AndValidRequestSucceeds() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var requestType = assembly.GetType("Sample.App.CreateClientCommand");
        requestType.Should().NotBeNull();
        var handlerInterface = typeof(IHandler<,>).MakeGenericType(requestType!, typeof(Result<Unit>));
        var handler = scope.ServiceProvider.GetRequiredService(handlerInterface);

        var invalidResult = await InvokeAsync(
            handlerInterface, handler, CreateCommand(requestType!, "ab", "not-an-email"));

        invalidResult.IsSuccess.Should().BeFalse();
        invalidResult.Error.Kind.Should().Be(ErrorKind.Validation);
        var data = invalidResult.Error.Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.FieldErrors.Should().NotBeNull();
        data.FieldErrors!.Keys.Should().BeEquivalentTo("name", "email");

        var validResult = await InvokeAsync(
            handlerInterface, handler, CreateCommand(requestType!, "Acme Inc.", "billing@acme.test"));

        validResult.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task StreamRequest_FailsThroughGeneratedStreamValidationPipeline() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var requestType = assembly.GetType("Sample.App.ExportClientsRequest")!;
        var streamType = typeof(IStreamHandler<,>).MakeGenericType(requestType, typeof(string));
        var request = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Page")!.SetValue(request, 0);

        var boxedValueTask = streamType.GetMethod("HandleAsync")!.Invoke(
            scope.ServiceProvider.GetRequiredService(streamType), [request, Ct])!;
        var task = (Task)boxedValueTask.GetType().GetMethod("AsTask")!.Invoke(boxedValueTask, null)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

        ((bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!).Should().BeFalse();
        var error = result.GetType().GetProperty("Error")!.GetValue(result)!;
        error.GetType().GetProperty("Kind")!.GetValue(error).Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public async Task ValidationRoot_GetsModuleMetadata_AndValidatesThroughTheSeam() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var rowType = assembly.GetType("Sample.App.ClientImportRow")!;

        var invalid = await validator.ValidateAsync(rowType, CreateRow(rowType, "x"), Ct);
        var valid = await validator.ValidateAsync(rowType, CreateRow(rowType, "AT-01"), Ct);

        invalid.Should().NotBeNull();
        invalid!.FieldErrors.Keys.Should().BeEquivalentTo("code");
        valid.Should().BeNull();
    }

    [Fact]
    public async Task HasMetadata_IsTrueForARequestAndARoot_AndFalseForAnUnknownType() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();

        validator.HasMetadata(assembly.GetType("Sample.App.CreateClientCommand")!).Should().BeTrue();
        validator.HasMetadata(assembly.GetType("Sample.App.ClientImportRow")!).Should().BeTrue();
        validator.HasMetadata(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public async Task HasMetadata_IsFalseForATypeOfADisabledModule() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        // A disabled module's ConfigureDefaultServices never runs, so its resolver is never registered.
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();

        validator.HasMetadata(assembly.GetType("Sample.App.CreateClientCommand")!).Should().BeFalse();
        validator.HasMetadata(assembly.GetType("Sample.App.ClientImportRow")!).Should().BeFalse();
    }

    [Fact]
    public async Task RecordsInARequestList_AreValidatedAtTheirIndexedPath() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var commandType = assembly.GetType("Sample.App.ImportHandoversCommand")!;
        var rowType = assembly.GetType("Sample.App.HandoverRow")!;

        object Command(params (string Code, int Count)[] rows) {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType))!;
            foreach (var (code, count) in rows) {
                var row = Activator.CreateInstance(rowType, code)!;
                rowType.GetProperty("Count")!.SetValue(row, count);
                list.Add(row);
            }

            var command = Activator.CreateInstance(commandType)!;
            commandType.GetProperty("Rows")!.SetValue(command, list);
            return command;
        }

        var invalid = await validator.ValidateAsync(commandType, Command(("AT-01", 1), ("x", 0)), Ct);
        var valid = await validator.ValidateAsync(commandType, Command(("AT-01", 1), ("AT-02", 5)), Ct);

        invalid.Should().NotBeNull();
        invalid!.FieldErrors.Keys.Should().BeEquivalentTo("rows[1].code", "rows[1].count");
        valid.Should().BeNull();
    }

    [Fact]
    public async Task AllowedValuesOnANullableMember_LetsNullPass_AndStillRefusesAnUnlistedValue() {
        var assembly = CompileAndLoad();
        var services = new ServiceCollection();
        services.AddElarionValidation();
        ConfigureGeneratedModuleServices(assembly, services);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var queryType = assembly.GetType("Sample.App.StatsQuery")!;

        object Query(int? days, string? unit, int? level) {
            var query = Activator.CreateInstance(queryType)!;
            queryType.GetProperty("Days")!.SetValue(query, days);
            queryType.GetProperty("Span")!.SetValue(query, unit);
            queryType.GetProperty("Level")!.SetValue(query, level);
            return query;
        }

        (await validator.ValidateAsync(queryType, Query(null, null, null), Ct)).Should().BeNull();
        (await validator.ValidateAsync(queryType, Query(90, "week", 2), Ct)).Should().BeNull();

        var invalid = await validator.ValidateAsync(queryType, Query(31, "day", 3), Ct);
        invalid.Should().NotBeNull();
        invalid!.FieldErrors.Keys.Should().BeEquivalentTo("days", "span", "level");
    }

    private static object CreateRow(Type rowType, string code) {
        var row = Activator.CreateInstance(rowType)!;
        rowType.GetProperty("Code")!.SetValue(row, code);
        return row;
    }

    /// <summary>Calls the generated module skeleton exactly like a host bootstrapper would:
    /// <c>Sample.App.AppModuleElarionModuleServices.ConfigureDefaultServices(services)</c> registers the
    /// module's handlers (with the auto-attached validation decorator) and its generated resolver.</summary>
    private static void ConfigureGeneratedModuleServices(Assembly assembly, IServiceCollection services) {
        var moduleServices = assembly.GetType("Sample.App.AppModuleElarionModuleServices");
        moduleServices.Should().NotBeNull();
        var configure = moduleServices!.GetMethod(
            "ConfigureDefaultServices", BindingFlags.Public | BindingFlags.Static);
        configure.Should().NotBeNull();
        configure!.Invoke(null, [services]);
    }

    private static object CreateCommand(Type requestType, string name, string email) {
        // `required`/init-only members are compile-time contracts; reflection can still populate them.
        var command = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Name")!.SetValue(command, name);
        requestType.GetProperty("Email")!.SetValue(command, email);
        return command;
    }

    private static async Task<Result<Unit>> InvokeAsync(Type handlerInterface, object handler, object request) {
        var handleAsync = handlerInterface.GetMethod("HandleAsync")!;
        var valueTask = (ValueTask<Result<Unit>>)handleAsync.Invoke(handler, [request, Ct])!;
        var result = await valueTask;
        return result;
    }

    /// <summary>Runs the three cooperating generators over <see cref="Source"/> and loads the emitted
    /// assembly, so the test executes the byte-for-byte generated registrations rather than a hand-written
    /// mirror of them.</summary>
    private static Assembly CompileAndLoad() {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(Source, parseOptions, cancellationToken: Ct);
        var compilation = CSharpCompilation.Create(
            "Elarion.Tests.ValidationEndToEnd.Sample",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
                new HandlerRegistrationGenerator(),
                new StreamHandlerRegistrationGenerator(),
                new ValidationResolverGenerator(),
                new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics,
            Ct);

        generatorDiagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        using var stream = new MemoryStream();
        var emitResult = outputCompilation.Emit(stream, cancellationToken: Ct);
        emitResult.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        emitResult.Success.Should().BeTrue();

        return Assembly.Load(stream.ToArray());
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)SharedMetadataReferences.FromFile(path))
            .ToArray();
    }
}
