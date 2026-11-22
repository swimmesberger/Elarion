using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ErrorContractGeneratorTests {
    private const string Header =
        """
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Idempotency;

        namespace Sample.Errors;

        public sealed record TokenProblem(string Reason);

        public enum ExportedChange { OnlyHere, Reexport }

        """;

    private static string Handler(string attributes, string response = "public sealed record Response(string Name);") =>
        Header
        + $$"""
            {{attributes}}
            [Handler("tokens.redeem")]
            public sealed class Redeem : IHandler<Redeem.Command, Result<Redeem.Response>> {
                public sealed record Command(string Token);
                {{response}}
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(AppError.NotFound("x"));
            }
            """;

    [Fact]
    public void DeclaredErrors_ArePublishedInTheManifest_WithKindAndPayload() {
        var generated = Run(Handler(
            """
            [ProducesError("token.malformed", ErrorKind.Validation, typeof(TokenProblem))]
            [ProducesError(ErrorKind.NotFound)]
            """), out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        generated.Should().Contain("token.malformed").And.Contain("global::Sample.Errors.TokenProblem")
            .And.Contain("not_found").And.Contain("NotFound");
    }

    [Fact]
    public void RequiresDecision_IsPublishedAsADecisionRequiredErrorWithItsAnswerShape() {
        var generated = Run(Handler(
            """
            [RequiresDecision("invoice.exported_change", Options = typeof(ExportedChange), DataType = typeof(TokenProblem))]
            [RequiresDecision("tokens.possible_duplicate", PerItem = true)]
            """), out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        ContractOf(generated, "tokens.redeem").Should().Equal(
            "invoice.exported_change:DecisionRequired;0|onlyHere,reexport:global::Sample.Errors.TokenProblem",
            "tokens.possible_duplicate:DecisionRequired;1|confirm:");
    }

    [Fact]
    public void RequiresDecision_WithOptionsThatAreNotAnEnum_IsReported() {
        Run(Handler("""[RequiresDecision("invoice.exported_change", Options = typeof(TokenProblem))]"""),
            out var diagnostics);

        diagnostics.Should().ContainSingle(d => d.Id == "ELERR003");
    }

    [Fact]
    public void IdempotentHandler_ImpliesTheIdempotencyErrors() {
        var generated = Run(Handler("[Idempotent]"), out _);

        generated.Should().Contain("idempotency.key_required")
            .And.Contain("idempotency.in_progress").And.Contain("idempotency.key_reused");
    }

    [Fact]
    public void PreviewableCommand_ImpliesThePreviewErrors_WithTheOutcomeAsTheChangedPayload() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Previews;

            namespace Sample.Errors;

            [Handler("imports.commit")]
            public sealed class Import : IHandler<Import.Command, Result<Import.Outcome>> {
                public sealed record Command(bool Preview = false, string? PreviewFingerprint = null) : IPreviewableRequest;

                public sealed record Outcome(int NewRows) : IPreviewOutcome<Outcome> {
                    public string? PreviewFingerprint { get; init; }
                    public Outcome WithPreviewFingerprint(string? fingerprint) => this with { PreviewFingerprint = fingerprint };
                }

                public ValueTask<Result<Outcome>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Outcome>>(new Outcome(1));
            }

            // A previewable request whose response cannot carry a fingerprint gets no decorator (ELPREV001, reported
            // by the handler generator), so no preview errors either.
            [Handler("imports.plain")]
            public sealed class Plain : IHandler<Plain.Command, Result<string>> {
                public sealed record Command(bool Preview = false, string? PreviewFingerprint = null) : IPreviewableRequest;

                public ValueTask<Result<string>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<string>>("x");
            }
            """;

        var generated = Run(source, out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        ContractOf(generated, "imports.commit").Should().Equal(
            "preview.changed:Conflict:global::Sample.Errors.Import.Outcome", "preview.unsupported:Internal:");
        ContractOf(generated, "imports.plain").Should().BeEmpty();
    }

    [Fact]
    public void RequirePermission_ImpliesUnauthorizedAndForbidden() {
        var generated = Run(Handler("[Elarion.Abstractions.Authorization.RequirePermission(\"a\", \"read\")]"), out _);

        generated.Should().Contain("unauthorized").And.Contain("forbidden");
    }

    [Fact]
    public void RequireResourceDeniedAsNotFound_ImpliesNotFound() {
        var hidden = Run(Handler(
            "[Elarion.Abstractions.Authorization.RequireResource(typeof(Redeem), Id = \"Token\", " +
            "DenyAs = Elarion.Abstractions.Authorization.ResourceDenial.NotFound)]"), out _);
        var forbidden = Run(Handler(
            "[Elarion.Abstractions.Authorization.RequireResource(typeof(Redeem), Id = \"Token\")]"), out _);

        ContractOf(hidden, "tokens.redeem").Should().Equal(
            "forbidden:Forbidden:", "not_found:NotFound:", "unauthorized:Unauthorized:");
        ContractOf(forbidden, "tokens.redeem").Should().Equal("forbidden:Forbidden:", "unauthorized:Unauthorized:");
    }

    [Fact]
    public void InvalidErrorCode_IsReported() {
        Run(Handler("[ProducesError(\"Token.Bad\", ErrorKind.Validation)]"), out var diagnostics);

        diagnostics.Should().ContainSingle(d => d.Id == "ELERR001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ConflictingDeclarationsOfOneCode_AreReported() {
        Run(Handler(
            """
            [ProducesError("seat.taken", ErrorKind.Conflict)]
            [ProducesError("seat.taken", ErrorKind.BusinessRule)]
            """), out var diagnostics);

        diagnostics.Should().ContainSingle(d => d.Id == "ELERR002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ResponseMember_NonNullableWithIgnoreCondition_IsReported() {
        Run(Handler(
            "",
            """
            public sealed record Response(string Name) {
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                public int Count { get; init; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string Required { get; init; } = "";
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string? Fine { get; init; }
            }
            """), out var diagnostics);

        diagnostics.Where(d => d.Id == "ELRPC004").Select(d => d.GetMessage())
            .Should().HaveCount(2)
            .And.Contain(m => m.Contains("Response.Count"))
            .And.Contain(m => m.Contains("Response.Required"));
    }

    [Fact]
    public void ErrorContractOutput_IsDeterministic_AndCachedAfterAnIrrelevantEdit() {
        var source = Handler(
            """
            [ProducesError("b.two", ErrorKind.Conflict)]
            [ProducesError("a.one", ErrorKind.Conflict)]
            """);
        Run(source, out _).Should().Be(Run(source, out _));

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionManifestGenerator(), source, "ManifestRpcMethods", "Manifest");
    }

    [Fact]
    public void KindOnlyDeclaration_UsesTheRuntimeDefaultCodeOfEveryKind() {
        foreach (var kind in Enum.GetValues<ErrorKind>()) {
            var generated = Run(Handler($"[ProducesError(ErrorKind.{kind})]"), out _);

            generated.Should().Contain(ErrorCodes.ForKind(kind), kind.ToString());
        }
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("a.b_c", true)]
    [InlineData("v2.x9", true)]
    [InlineData("Ab", false)]
    [InlineData("a..b", false)]
    [InlineData("1a", false)]
    [InlineData("a-b", false)]
    [InlineData("a.", false)]
    public void ErrorCodeFormat_MatchesTheRuntimeRule(string code, bool valid) {
        ErrorCodes.IsValid(code).Should().Be(valid);
        var diagnostics = new List<Diagnostic>();
        Run(Handler($"[ProducesError(\"{code}\", ErrorKind.Conflict)]"), out var found);
        found.Any(d => d.Id == "ELERR001").Should().Be(!valid);
    }

    // A multi-handler source for the scope tests: two modules, an assembly-wide pipeline with two decorators, and a
    // handler that overrides the pipeline. Each test fills in its own assembly, module, decorator and handler
    // declarations.
    private static string Scoped(
        string assemblyAttributes = "",
        string ordersModuleAttributes = "",
        string conflictDecoratorAttributes = "",
        string commandDecoratorAttributes = "",
        string handlerAttributes = "") =>
        $$"""
          using System;
          using System.Threading;
          using System.Threading.Tasks;
          using Elarion.Abstractions;
          using Elarion.Abstractions.Modules;
          using Elarion.Abstractions.Pipeline;

          {{assemblyAttributes}}
          [assembly: Sample.Scoped.AppPipeline]

          namespace Sample.Scoped {
              public sealed record Conflicted(string Constraint);

              {{conflictDecoratorAttributes}}
              public sealed class ConflictDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                  : IHandler<TRequest, TResponse> {
                  public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                      inner.HandleAsync(request, ct);
              }

              {{commandDecoratorAttributes}}
              public sealed class CommandDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                  : IHandler<TRequest, TResponse> where TRequest : ICommand {
                  public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                      inner.HandleAsync(request, ct);
              }

              [DecoratorList(typeof(ConflictDecorator<,>), typeof(CommandDecorator<,>))]
              [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
              public sealed class AppPipelineAttribute : Attribute;

              [DecoratorList]
              [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
              public sealed class BarePipelineAttribute : Attribute;
          }

          namespace Sample.Scoped.Orders {
              {{ordersModuleAttributes}}
              [AppModule("Orders")]
              public static class OrdersModule { }

              {{handlerAttributes}}
              [Handler("orders.get")]
              public sealed class GetOrder : IHandler<GetOrder.Query, Result<GetOrder.Response>> {
                  public sealed record Query(string Id) : IQuery;
                  public sealed record Response(string Id);
                  public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                      ValueTask.FromResult<Result<Response>>(new Response(request.Id));
              }

              [Handler("orders.place")]
              public sealed class PlaceOrder : IHandler<PlaceOrder.Command, Result<PlaceOrder.Response>> {
                  public sealed record Command(string Id) : ICommand;
                  public sealed record Response(string Id);
                  public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                      ValueTask.FromResult<Result<Response>>(new Response(request.Id));
              }

              [Sample.Scoped.BarePipeline]
              [Handler("orders.ping")]
              public sealed class PingOrders : IHandler<PingOrders.Command, Result<PingOrders.Response>> {
                  public sealed record Command : ICommand;
                  public sealed record Response(bool Ok);
                  public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                      ValueTask.FromResult<Result<Response>>(new Response(true));
              }
          }

          namespace Sample.Scoped.Billing {
              [AppModule("Billing")]
              public static class BillingModule { }

              [Handler("billing.get")]
              public sealed class GetBill : IHandler<GetBill.Query, Result<GetBill.Response>> {
                  public sealed record Query(string Id) : IQuery;
                  public sealed record Response(string Id);
                  public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                      ValueTask.FromResult<Result<Response>>(new Response(request.Id));
              }
          }
          """;

    [Fact]
    public void AssemblyDefaults_ApplyToEveryOperation_AndNeverOverrideTheHandlersOwnDeclaration() {
        var generated = Run(Scoped(
            assemblyAttributes:
            """
            [assembly: ProducesError(ErrorKind.NotFound)]
            [assembly: ProducesError(ErrorKind.Conflict)]
            """,
            handlerAttributes: "[ProducesError(ErrorKind.NotFound, typeof(Sample.Scoped.Conflicted))]"), out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        ContractOf(generated, "billing.get").Should().Equal("conflict:Conflict:", "not_found:NotFound:");
        ContractOf(generated, "orders.ping").Should().Equal("conflict:Conflict:", "not_found:NotFound:");
        ContractOf(generated, "orders.get").Should().Equal(
            "conflict:Conflict:", "not_found:NotFound:global::Sample.Scoped.Conflicted");
    }

    [Fact]
    public void ModuleDefaults_ApplyOnlyToTheModulesOperations_AndOverrideTheAssembly() {
        var generated = Run(Scoped(
            assemblyAttributes: "[assembly: ProducesError(ErrorKind.BusinessRule)]",
            ordersModuleAttributes:
            """
            [ProducesError(ErrorKind.BusinessRule, typeof(Sample.Scoped.Conflicted))]
            [ProducesError("order.closed", ErrorKind.Conflict)]
            """), out _);

        ContractOf(generated, "orders.get").Should().Equal(
            "business_rule:BusinessRule:global::Sample.Scoped.Conflicted", "order.closed:Conflict:");
        ContractOf(generated, "billing.get").Should().Equal("business_rule:BusinessRule:");
    }

    [Fact]
    public void DecoratorDeclarations_ApplyToTheOperationsTheDecoratorWraps() {
        var generated = Run(Scoped(
            conflictDecoratorAttributes: "[ProducesError(ErrorKind.Conflict, typeof(Conflicted))]",
            commandDecoratorAttributes: "[ProducesError(\"order.throttled\", ErrorKind.BusinessRule)]",
            ordersModuleAttributes: "[ProducesError(ErrorKind.Conflict)]"), out var diagnostics);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        // The decorator's declaration (with its payload) beats the module's for the same code.
        ContractOf(generated, "orders.get").Should().Equal("conflict:Conflict:global::Sample.Scoped.Conflicted");
        // The command-only decorator is excluded from queries by its constraint, but wraps commands.
        ContractOf(generated, "orders.place").Should().Equal(
            "conflict:Conflict:global::Sample.Scoped.Conflicted", "order.throttled:BusinessRule:");
        // A handler-level pipeline replaces the assembly's, so neither decorator wraps it.
        ContractOf(generated, "orders.ping").Should().Equal("conflict:Conflict:");
        ContractOf(generated, "billing.get").Should().Equal("conflict:Conflict:global::Sample.Scoped.Conflicted");
    }

    [Fact]
    public void ImpliedErrors_RankAboveDefaults() {
        var generated = Run(Scoped(
            assemblyAttributes: "[assembly: ProducesError(ErrorKind.Unauthorized, typeof(Sample.Scoped.Conflicted))]",
            handlerAttributes: "[Elarion.Abstractions.Authorization.RequirePermission(\"orders\", \"read\")]"), out _);

        ContractOf(generated, "orders.get").Should().Equal("forbidden:Forbidden:", "unauthorized:Unauthorized:");
        ContractOf(generated, "billing.get").Should().Equal(
            "unauthorized:Unauthorized:global::Sample.Scoped.Conflicted");
    }

    [Fact]
    public void InvalidOrConflictingScopeDeclarations_AreReportedOncePerScope() {
        Run(Scoped(
            assemblyAttributes: "[assembly: ProducesError(\"Bad.Code\", ErrorKind.Conflict)]",
            ordersModuleAttributes:
            """
            [ProducesError("order.closed", ErrorKind.Conflict)]
            [ProducesError("order.closed", ErrorKind.BusinessRule)]
            """,
            conflictDecoratorAttributes: "[ProducesError(\"also-bad\", ErrorKind.Conflict)]"), out var diagnostics);

        diagnostics.Where(d => d.Id == "ELERR001").Select(d => d.GetMessage())
            .Should().HaveCount(2)
            .And.Contain(m => m.Contains("'ErrorContracts'") && m.Contains("'Bad.Code'"))
            .And.Contain(m => m.Contains("ConflictDecorator") && m.Contains("'also-bad'"));
        diagnostics.Should().ContainSingle(d => d.Id == "ELERR002")
            .Which.GetMessage().Should().Contain("OrdersModule").And.Contain("'order.closed'");
    }

    [Fact]
    public void ScopedContract_IsCachedAfterAnIrrelevantEdit() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionManifestGenerator(),
            Scoped(
                assemblyAttributes: "[assembly: ProducesError(ErrorKind.NotFound)]",
                conflictDecoratorAttributes: "[ProducesError(ErrorKind.Conflict)]"),
            "ManifestRpcMethods", "ManifestRpcErrorScopes", "Manifest");
    }

    [Fact]
    public void ScopedContract_FollowsAnEditToAnotherFile() {
        // The defaults live outside the handlers' file: the per-handler discovery stays cached while the scope stage
        // re-reads the declarations from the current compilation.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var ct = TestContext.Current.CancellationToken;
        var handlers = CSharpSyntaxTree.ParseText(Scoped(), parseOptions, cancellationToken: ct);
        var defaults = CSharpSyntaxTree.ParseText("// no defaults yet", parseOptions, cancellationToken: ct);
        var compilation = CreateCompilation([handlers, defaults]);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true));
        driver = driver.RunGenerators(compilation, ct);
        ContractOf(Manifest(driver), "orders.get").Should().BeEmpty();

        var edited = CSharpSyntaxTree.ParseText(
            "[assembly: Elarion.Abstractions.ProducesError(Elarion.Abstractions.ErrorKind.NotFound)]", parseOptions,
            cancellationToken: ct);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(defaults, edited), ct);

        ContractOf(Manifest(driver), "orders.get").Should().Equal("not_found:NotFound:");
        driver.GetRunResult().Results.Single().TrackedSteps["ManifestRpcMethods"]
            .SelectMany(step => step.Outputs)
            .Should().OnlyContain(output => output.Reason == IncrementalStepRunReason.Cached);
    }

    // One operation's published contract as "code:Kind:payload" entries, decoded from its manifest entry: a
    // length-prefixed field list whose last field is the errors blob of code/kind/payload triples.
    private static string[] ContractOf(string manifest, string operation) {
        const string marker = "\"Elarion.Manifest.RpcMethod.v2\", \"";
        foreach (var line in manifest.Split('\n')) {
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;

            var value = line.Substring(start + marker.Length);
            var fields = Decode(value.Substring(0, value.LastIndexOf("\")]", StringComparison.Ordinal)));
            if (fields[0] != operation) continue;

            // The error contract is field 12; later fields were appended after it (client metadata).
            var errors = Decode(fields[12]!);
            var result = new List<string>();
            for (var i = 0; i < errors.Count; i += 3)
                result.Add($"{errors[i]}:{errors[i + 1]}:{errors[i + 2]}");

            return [.. result];
        }

        throw new InvalidOperationException($"No manifest entry for operation '{operation}'.");
    }

    private static List<string?> Decode(string value) {
        var fields = new List<string?>();
        var position = 0;
        while (position < value.Length) {
            var colon = value.IndexOf(':', position);
            var length = int.Parse(
                value.AsSpan(position, colon - position), System.Globalization.CultureInfo.InvariantCulture);
            position = colon + 1;
            if (length < 0) {
                fields.Add(null);
                continue;
            }

            fields.Add(value.Substring(position, length));
            position += length;
        }

        return fields;
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees) {
        return CSharpCompilation.Create(
            "ErrorContracts",
            trees,
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(p => (MetadataReference)SharedMetadataReferences.FromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string Manifest(GeneratorDriver driver) {
        return driver.GetRunResult().GeneratedTrees
            .SingleOrDefault(t => Path.GetFileName(t.FilePath) == "ElarionManifest.g.cs")?.GetText().ToString()
            ?? string.Empty;
    }

    private static string Run(string source, out IReadOnlyList<Diagnostic> diagnostics) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CreateCompilation([CSharpSyntaxTree.ParseText(source, parseOptions)]);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
        diagnostics = result.Diagnostics;

        return result.GeneratedTrees
            .SingleOrDefault(t => Path.GetFileName(t.FilePath) == "ElarionManifest.g.cs")?.GetText().ToString()
            ?? string.Empty;
    }
}
