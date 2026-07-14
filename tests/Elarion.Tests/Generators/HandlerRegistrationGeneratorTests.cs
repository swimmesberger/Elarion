using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class HandlerRegistrationGeneratorTests {
    [Fact]
    public void GenerateRegistration_HandlerWithoutPipelineAttribute_UsesAssemblyPipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("TransactionDecorator");
        generated.Should().Contain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_ModulePipelineAttribute_OverridesAssemblyPipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "[Sample.Pipeline.ReadOnlyPipeline]",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().NotContain("TransactionDecorator");
        generated.Should().NotContain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_HandlerPipelineAttribute_OverridesModulePipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "[Sample.Pipeline.ReadOnlyPipeline]",
            handlerPipelineAttribute: "[Sample.Pipeline.DefaultPipeline]");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("TransactionDecorator");
        generated.Should().Contain("DbConstraintDecorator");
        generated.Should().Contain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_UseElarion_EmitsModuleAggregator() {
        var source = CreateSource(
            """
            [assembly: Elarion.Abstractions.UseElarion]
            [assembly: Sample.Pipeline.DefaultPipeline]
            """,
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var result = GenerateHandlerRegistrationRunResult(source);
        var generated = GetGeneratedSource(result, "SalesHandlerExtensions.g.cs");

        generated.Should().Contain("public static IServiceCollection AddSalesHandlers(");
        generated.Should().Contain("Sample.Modules.Sales.Handlers.CreateOrderRegistration.AddCreateOrder(services, lifetime);");
    }

    [Fact]
    public void GenerateRegistration_CacheableHandler_EmitsCacheDecoratorAndPolicy() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute:
            """
            [Sample.Pipeline.ReadOnlyPipeline]
            [Elarion.Abstractions.Caching.Cacheable(
                "sample:sales",
                DurationSeconds = 120,
                Scope = Elarion.Abstractions.Caching.HandlerCacheScope.Global)]
            """);

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("CacheDecorator");
        generated.Should().Contain("CreateOrderCachePolicy");
        generated.Should().Contain("TimeSpan.FromSeconds(120)");
        generated.Should().Contain("\"sample:sales\"");
        generated.Should().Contain("HandlerCacheKey.Part(\"CustomerId\", request.CustomerId)");
        generated.IndexOf("CacheDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ValidationDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_CacheInvalidatingHandler_EmitsInvalidationDecoratorOutermost() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute:
            """
            [Elarion.Abstractions.Caching.CacheInvalidate(
                "sample:sales",
                Scope = Elarion.Abstractions.Caching.HandlerCacheScope.Global)]
            """);

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("CacheInvalidationDecorator");
        generated.Should().Contain("CreateOrderCacheInvalidationPolicy");
        generated.Should().Contain("\"sample:sales\"");
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("CacheInvalidationDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_ResilientHandler_EmitsResilienceDecoratorOutsidePipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: """[Elarion.Abstractions.Resilience.Resilient("invoice-email")]""");

        var generated = GenerateHandlerRegistrationSource(source);

        generated.Should().Contain("ResilienceDecorator");
        generated.Should().NotContain("AddElarionResilience");
        generated.Should().Contain("IResiliencePipelineRunner");
        generated.Should().Contain("ResiliencePolicyReference { Name = \"invoice-email\" }");
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("ResilienceDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_AnyHandler_EmitsTracingDecoratorOutermost() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        // Tracing is applied unconditionally to every handler, with no opt-in attribute.
        generated.Should().Contain("global::Elarion.Pipeline.TracingDecorator<");
        generated.Should().Contain("\"CreateOrder\"");
        // Tracing is emitted last so its span parents the pipeline decorators.
        generated.IndexOf("TransactionDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("TracingDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_HandlerWithoutPipeline_StillEmitsTracingDecorator() {
        var source = CreateSource(
            assemblyPipelineAttribute: "",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        // Default-on: tracing wraps the handler even when no other decorator applies.
        generated.Should().Contain("global::Elarion.Pipeline.TracingDecorator<");
        generated.Should().NotContain("TransactionDecorator");
        generated.Should().NotContain("ValidationDecorator");
    }

    [Fact]
    public void GenerateRegistration_AnyHandler_EmitsContextEnrichmentJustInsideTracing() {
        var source = CreateSource(
            assemblyPipelineAttribute: "",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        var generated = GenerateHandlerRegistrationSource(source);

        // User/context enrichment is on by default for every handler (no opt-in attribute), like tracing.
        generated.Should().Contain("global::Elarion.Pipeline.HandlerContextEnrichmentDecorator<");
        // Emitted just before tracing (inner→outer), so at runtime it runs just inside the handler span: its tags
        // land on that span and its log scope wraps authorization/validation/handler.
        generated.IndexOf("HandlerContextEnrichmentDecorator", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("TracingDecorator", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRegistration_ConstrainedDecorator_AppliesOnlyToMatchingRequestKind() {
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.CommandPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed class CommandOnlyDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse>
                    where TRequest : ICommand {
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(CommandOnlyDecorator<,>))]
                [System.AttributeUsage(System.AttributeTargets.Assembly | System.AttributeTargets.Class)]
                public sealed class CommandPipelineAttribute : System.Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }

                public sealed record ReadThingQuery(int Id) : IQuery;
                public sealed record ReadThingResponse(string Name);

                public sealed class ReadThing : IHandler<ReadThingQuery, Result<ReadThingResponse>> {
                    public ValueTask<Result<ReadThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<ReadThingResponse>.Success(new ReadThingResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var doThing = GetGeneratedSource(result, "Sample_App_DoThing.g.cs");
        var readThing = GetGeneratedSource(result, "Sample_App_ReadThing.g.cs");

        // The `where TRequest : ICommand` decorator wraps the command handler but is filtered out of the query handler.
        doThing.Should().Contain("global::Sample.App.CommandOnlyDecorator<");
        readThing.Should().NotContain("CommandOnlyDecorator");
    }

    [Fact]
    public void GenerateRegistration_DecoratorWithHandlerMetadata_InjectsConcreteHandlerTypeRegardlessOfPosition() {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.AuthPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                // Reads the handler's attributes through HandlerMetadata, so it sees the concrete handler at
                // ANY position. Here it is the OUTERMOST decorator — the inner.GetType() approach would fail
                // open from this position because `inner` is the next decorator, not the handler.
                public sealed class AuthorizationDecorator<TRequest, TResponse>(
                    IHandler<TRequest, TResponse> inner,
                    HandlerMetadata metadata)
                    : IHandler<TRequest, TResponse> {
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                public sealed class PlainDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse> {
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                // Authorization is first => outermost; a plain decorator is nested below it.
                [DecoratorList(typeof(AuthorizationDecorator<,>), typeof(PlainDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class AuthPipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var generated = GetGeneratedSource(result, "Sample_App_DoThing.g.cs");

        // The generator supplies a metadata singleton built from the CONCRETE handler type...
        generated.Should().Contain(
            "private static readonly global::Elarion.Abstractions.Pipeline.HandlerMetadata __handlerMetadata =");
        generated.Should().Contain(
            "new(typeof(global::Sample.App.DoThing), typeof(global::Sample.App.DoThingCommand), "
            + "typeof(global::Elarion.Abstractions.Result<global::Sample.App.DoThingResponse>),");
        // ...plus a late-bound accessor onto the resolved-pipeline cache.
        generated.Should().Contain(
            "static () => __pipeline ?? global::System.Array.Empty<global::Elarion.Abstractions.Pipeline.PipelineStep>());");
        // ...and passes it to the (outermost) decorator instead of resolving it from DI.
        generated.Should().Contain("global::Sample.App.AuthorizationDecorator<");
        generated.Should().Contain("(handler, __handlerMetadata)");
        generated.Should().NotContain("GetRequiredService<global::Elarion.Abstractions.Pipeline.HandlerMetadata>");

        // The emitted registration is valid C#: compile the source plus this handler's generated tree and
        // assert no errors — this proves the HandlerMetadata type, field, and constructor usage are correct.
        // (Only this tree is compiled; the cross-generator module-services partial skeleton is emitted by
        // ModuleDefaultServicesGenerator, which does not run in this isolated single-generator test.)
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var full = CSharpCompilation.Create(
            "HandlerMetadataCompile",
            [
                CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(generated, parseOptions, cancellationToken: TestContext.Current.CancellationToken)
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        full.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void GenerateRegistration_ResponseConstrainedDecorator_AttachesOnlyToResultReturningHandlers() {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.FailurePipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                // The self-referential `where TResponse : IResultFailureFactory<TResponse>` constraint scopes the
                // decorator to Result-returning handlers and lets it build a failure via static TResponse.Failure.
                public sealed class FailureMappingDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse>
                    where TResponse : IResultFailureFactory<TResponse> {
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(FailureMappingDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class FailurePipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }

                // Returns a bare (non-Result) response, so the constraint must elide the decorator.
                public sealed record PlainQuery(int Id) : IQuery;
                public sealed record PlainResponse(string Name);
                public sealed class Plain : IHandler<PlainQuery, PlainResponse> {
                    public ValueTask<PlainResponse> HandleAsync(PlainQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(new PlainResponse("x"));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var doThing = GetGeneratedSource(result, "Sample_App_DoThing.g.cs");
        var plain = GetGeneratedSource(result, "Sample_App_Plain.g.cs");

        // Attaches to the Result-returning handler (Result<T> implements IResultFailureFactory<Result<T>>)...
        doThing.Should().Contain("global::Sample.App.FailureMappingDecorator<");
        // ...and is filtered out of the handler whose response does not implement the interface.
        plain.Should().NotContain("FailureMappingDecorator");
    }

    [Fact]
    public void GenerateRegistration_AppliesToPredicate_EmitsCachedRuntimeConditional() {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Messaging;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.UnitOfWorkPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                // Attaches to commands and integration-event handlers, but not queries — a union no `where` can state.
                public sealed class TxDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse> {
                    public static bool AppliesTo(HandlerMetadata handler) =>
                        handler.RequestType.IsAssignableTo(typeof(ICommand)) ||
                        handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent));
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(TxDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class UnitOfWorkPipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }

                public sealed record ReadThingQuery(int Id) : IQuery;
                public sealed class ReadThing : IHandler<ReadThingQuery, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(ReadThingQuery request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }

                public sealed record SomethingHappened(int Id) : IIntegrationEvent;
                public sealed class OnSomething : IHandler<SomethingHappened, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(SomethingHappened request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var doThing = GetGeneratedSource(result, "Sample_App_DoThing.g.cs");
        var readThing = GetGeneratedSource(result, "Sample_App_ReadThing.g.cs");

        // The predicate is called once (cached) per closed handler type with that handler's metadata (which carries
        // the request type), so each handler's predicate sees its own request type...
        doThing.Should().Contain("private static readonly bool __pipelineApplies0");
        doThing.Should().Contain(".AppliesTo(__handlerMetadata)");
        readThing.Should().Contain(".AppliesTo(__handlerMetadata)");
        doThing.Should().Contain("new(typeof(global::Sample.App.DoThing), typeof(global::Sample.App.DoThingCommand),");
        readThing.Should().Contain("new(typeof(global::Sample.App.ReadThing), typeof(global::Sample.App.ReadThingQuery),");
        // ...and the decorator is wrapped in a runtime conditional in every in-scope handler (attachment is decided at run time).
        doThing.Should().Contain("if (__pipelineApplies0)");
        readThing.Should().Contain("if (__pipelineApplies0)");
        doThing.Should().Contain("new global::Sample.App.TxDecorator<");
    }

    [Fact]
    public void GenerateRegistration_NonPublicAppliesTo_ReportsDiagnostic() {
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.BadPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed class BadDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse> {
                    // Not public: the generated registration cannot call it -> ELPIPE001.
                    private static bool AppliesTo(HandlerMetadata handler) => true;
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(BadDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class BadPipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "AppliesToDiagnostic",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();

        result.Diagnostics.Any(d => d.Id == "ELPIPE001" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateRegistration_LegacyAppliesToTypeSignature_ReportsDiagnostic() {
        // The older `AppliesTo(System.Type request)` form is no longer supported — there is one predicate
        // signature. It must fail loudly (ELPIPE002) rather than be silently ignored (which would attach the
        // decorator unconditionally).
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.LegacyPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed class LegacyDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse> {
                    public static bool AppliesTo(Type request) => true;
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(LegacyDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class LegacyPipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            "LegacyAppliesToDiagnostic",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();

        result.Diagnostics.Any(d => d.Id == "ELPIPE002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateRegistration_IHandlerOfT_RegistersResultUnitInterfaceWithTracing() {
        // The IHandler<T> sugar inherits IHandler<T, Result<Unit>> via a default interface method,
        // so the generator discovers and registers it as the two-arg interface with no special-casing.
        var source = """
            namespace Sample.Modules.Sales.Handlers {
                public sealed record Ping(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                public sealed class PingHandler : Elarion.Abstractions.IHandler<Ping> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        Ping request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var generated = GetGeneratedSource(result, "Sample_Modules_Sales_Handlers_PingHandler.g.cs");

        generated.Should().Contain(
            "global::Elarion.Abstractions.IHandler<global::Sample.Modules.Sales.Handlers.Ping, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>");
        generated.Should().Contain("global::Elarion.Pipeline.TracingDecorator<");
    }

    [Fact]
    public void GenerateRegistration_EventConsumer_RegistersKeyed_CommandStaysUnkeyed() {
        // ADR-0046: an event consumer registers its decorated pipeline KEYED by its FQN so multiple consumers
        // of one event coexist; a command/query stays UNKEYED — exactly one handler per request, injectable
        // typed-direct as IHandler<TReq, TResp>.
        var source = """
            namespace Sample.Modules.Sales.Handlers {
                public sealed record OrderPlaced(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;
                public sealed record DoThing(int Id) : Elarion.Abstractions.ICommand;

                public sealed class OnOrderPlaced : Elarion.Abstractions.IHandler<OrderPlaced> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        OrderPlaced request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }

                public sealed class DoThingHandler : Elarion.Abstractions.IHandler<DoThing, Elarion.Abstractions.Result> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        DoThing request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);

        var consumer = GetGeneratedSource(result, "Sample_Modules_Sales_Handlers_OnOrderPlaced.g.cs");
        consumer.Should().Contain("\"global::Sample.Modules.Sales.Handlers.OnOrderPlaced\",");
        consumer.Should().Contain("(sp, __key) => BuildPipeline(sp),");

        var command = GetGeneratedSource(result, "Sample_Modules_Sales_Handlers_DoThingHandler.g.cs");
        command.Should().Contain("sp => BuildPipeline(sp),");
        command.Should().NotContain("(sp, __key) =>");
    }

    [Fact]
    public void GenerateRegistration_OpenGenericHandler_IsSkippedAndEmitsCompilableCode() {
        // An open-generic handler-shaped type (a common generic test double) cannot be registered as a concrete
        // service: emitting a registration would reference its own type parameters as concrete types (CS0246).
        // This bites downstream because the bundled analyzer flows transitively into consumer test projects, so a
        // generic IHandler<,> helper would otherwise break the *test* build. The generator must skip it entirely.
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                public sealed class ThrowingHandler<TRequest, TResponse>(Exception ex)
                    : IHandler<TRequest, TResponse> {
                    public ValueTask<TResponse> HandleAsync(TRequest r, CancellationToken ct) => throw ex;
                }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);
                public sealed class DoThing : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);

        // The open-generic handler produces no registration file at all, and is never referenced by any generated
        // output (per-handler file, module aggregator, or default-services filler) — the pre-fix behavior emitted
        // a `ThrowingHandler<TRequest, TResponse>` registration that failed to compile (CS0246).
        result.GeneratedTrees.Should().NotContain(tree =>
            Path.GetFileName(tree.FilePath).Contains("ThrowingHandler", StringComparison.Ordinal));
        foreach (var tree in result.GeneratedTrees) {
            tree.GetText(TestContext.Current.CancellationToken).ToString()
                .Should().NotContain("ThrowingHandler");
        }

        // ...while the concrete handler is still registered as normal, and its registration compiles cleanly
        // alongside the source. Only the handler tree is compiled here — the cross-generator module-services
        // skeleton is emitted by ModuleDefaultServicesGenerator, which does not run in this single-generator test.
        var doThing = GetGeneratedSource(result, "Sample_App_DoThing.g.cs");
        doThing.Should().Contain("global::Sample.App.DoThing");

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var full = CSharpCompilation.Create(
            "OpenGenericHandlerCompile",
            [
                CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(doThing, parseOptions, cancellationToken: TestContext.Current.CancellationToken),
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        full.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void GenerateRegistration_IrrelevantEdit_ReusesPipeline() {
        var source = CreateSource(
            "[assembly: Sample.Pipeline.DefaultPipeline]",
            modulePipelineAttribute: "",
            handlerPipelineAttribute: "");

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(),
            source,
            "Handlers");
    }

    [Fact]
    public void GenerateRegistration_AppliesToHandlerMetadata_AttachesBasedOnHandlerAttributes() {
        // A *custom* decorator attaches based on the HANDLER's attributes via AppliesTo(HandlerMetadata) — the
        // same capability the framework's built-in decorators use, with no privileged generator magic.
        const string source =
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.Abstractions.Pipeline;

            [assembly: UseElarion]
            [assembly: Sample.App.AuditPipeline]

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                [AttributeUsage(AttributeTargets.Class)]
                public sealed class AuditableAttribute : Attribute { }

                public sealed class AuditDecorator<TRequest, TResponse>(IHandler<TRequest, TResponse> inner)
                    : IHandler<TRequest, TResponse> {
                    public static bool AppliesTo(HandlerMetadata handler) =>
                        handler.GetAttribute<AuditableAttribute>() is not null;
                    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) =>
                        inner.HandleAsync(request, ct);
                }

                [DecoratorList(typeof(AuditDecorator<,>))]
                [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
                public sealed class AuditPipelineAttribute : Attribute { }

                public sealed record DoThingCommand(int Id) : ICommand;
                public sealed record DoThingResponse(string Name);

                [Auditable]
                public sealed class AuditedHandler : IHandler<DoThingCommand, Result<DoThingResponse>> {
                    public ValueTask<Result<DoThingResponse>> HandleAsync(DoThingCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<DoThingResponse>.Success(new DoThingResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);
        var generated = GetGeneratedSource(result, "Sample_App_AuditedHandler.g.cs");

        // The predicate is invoked with the handler metadata (not typeof(request)), cached as a runtime conditional.
        generated.Should().Contain("global::Sample.App.AuditDecorator<global::Sample.App.DoThingCommand, global::Elarion.Abstractions.Result<global::Sample.App.DoThingResponse>>.AppliesTo(__handlerMetadata)");
        generated.Should().Contain("if (__pipelineApplies0)");
        generated.Should().Contain("new global::Sample.App.AuditDecorator<");
        // The metadata field is declared before the AppliesTo field that reads it (static initializer order).
        generated.IndexOf("HandlerMetadata __handlerMetadata", StringComparison.Ordinal)
            .Should().BeLessThan(generated.IndexOf("__pipelineApplies0", StringComparison.Ordinal));

        // The emitted registration is valid C#: AppliesTo(HandlerMetadata) and the metadata field resolve.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var full = CSharpCompilation.Create(
            "AppliesToMetadataCompile",
            [
                CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(generated, parseOptions, cancellationToken: TestContext.Current.CancellationToken)
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        full.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void GenerateRegistration_HandlerInDecoratorsNamespace_IsRegistered() {
        // Regression: the old exclusion matched any namespace CONTAINING "Decorators", silently skipping
        // registration for consumer handlers (e.g. MyApp.Decorators.Pricing) while the RPC map still routed
        // to them. Only Elarion's own pipeline decorators (Elarion.Pipeline) are excluded.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]

            namespace MyApp.Decorators.Pricing {
                [AppModule("Pricing")]
                public static class PricingModule { }

                public sealed record PriceCommand(int Id) : ICommand;
                public sealed record PriceResponse(string Name);

                public sealed class PriceCake : IHandler<PriceCommand, Result<PriceResponse>> {
                    public ValueTask<Result<PriceResponse>> HandleAsync(PriceCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<PriceResponse>.Success(new PriceResponse("priced")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);

        var registration = GetGeneratedSource(result, "MyApp_Decorators_Pricing_PriceCake.g.cs");
        registration.Should().Contain("public static IServiceCollection AddPriceCake(");

        var aggregation = GetGeneratedSource(result, "PricingHandlerExtensions.g.cs");
        aggregation.Should().Contain("MyApp.Decorators.Pricing.PriceCakeRegistration.AddPriceCake(services, lifetime);");
    }

    [Fact]
    public void GenerateRegistration_DuplicateModuleNames_KeepsOneWinnerWithoutCrashing() {
        // Regression: two [AppModule]s sharing one name produced two identical AddSource hint names
        // ({Name}HandlerExtensions.g.cs), failing the whole generator with an ArgumentException. The shared
        // module collection now keeps one deterministic winner (ordinal-first by metadata name).
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            [assembly: UseElarion]

            namespace Alpha {
                [AppModule("Sales")]
                public static class AlphaSalesModule { }

                public sealed record AlphaCommand(int Id) : ICommand;
                public sealed record AlphaResponse(string Name);

                public sealed class AlphaThing : IHandler<AlphaCommand, Result<AlphaResponse>> {
                    public ValueTask<Result<AlphaResponse>> HandleAsync(AlphaCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<AlphaResponse>.Success(new AlphaResponse("a")));
                }
            }

            namespace Beta {
                [AppModule("Sales")]
                public static class BetaSalesModule { }

                public sealed record BetaCommand(int Id) : ICommand;
                public sealed record BetaResponse(string Name);

                public sealed class BetaThing : IHandler<BetaCommand, Result<BetaResponse>> {
                    public ValueTask<Result<BetaResponse>> HandleAsync(BetaCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<BetaResponse>.Success(new BetaResponse("b")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);

        // Exactly one aggregation is emitted (no duplicate-hint crash), owned by the ordinal-first winner.
        result.GeneratedTrees
            .Count(tree => string.Equals(
                Path.GetFileName(tree.FilePath), "SalesHandlerExtensions.g.cs", StringComparison.Ordinal))
            .Should().Be(1);
        var aggregation = GetGeneratedSource(result, "SalesHandlerExtensions.g.cs");
        aggregation.Should().Contain("namespace Alpha;");
        aggregation.Should().Contain("AddAlphaThing(services, lifetime);");
        aggregation.Should().NotContain("BetaThing");

        // Both handlers still get their per-handler registration (typed-direct resolution stays intact).
        GetGeneratedSource(result, "Alpha_AlphaThing.g.cs").Should().Contain("AddAlphaThing(");
        GetGeneratedSource(result, "Beta_BetaThing.g.cs").Should().Contain("AddBetaThing(");
    }

    [Fact]
    public void GenerateRegistration_UnderscoreAndDottedNamespaces_ProduceDistinctHintNames() {
        // Regression: '.' → '_' hint sanitization collapsed `A.B_C.FooHandler` and `A.B.C.FooHandler` onto one
        // hint name — a duplicate AddSource crash. The ambiguous shape now carries a stable hash suffix.
        const string source =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            namespace A.B_C {
                public sealed record FooCommand(int Id) : ICommand;
                public sealed record FooResponse(string Name);

                public sealed class FooHandler : IHandler<FooCommand, Result<FooResponse>> {
                    public ValueTask<Result<FooResponse>> HandleAsync(FooCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<FooResponse>.Success(new FooResponse("x")));
                }
            }

            namespace A.B.C {
                public sealed record FooCommand(int Id) : ICommand;
                public sealed record FooResponse(string Name);

                public sealed class FooHandler : IHandler<FooCommand, Result<FooResponse>> {
                    public ValueTask<Result<FooResponse>> HandleAsync(FooCommand request, CancellationToken ct) =>
                        ValueTask.FromResult(Result<FooResponse>.Success(new FooResponse("x")));
                }
            }
            """;

        var result = GenerateHandlerRegistrationRunResult(source);

        var registrationFiles = result.GeneratedTrees
            .Select(tree => Path.GetFileName(tree.FilePath))
            .Where(name => name.StartsWith("A_B", StringComparison.Ordinal))
            .ToArray();
        registrationFiles.Should().HaveCount(2);
        registrationFiles.Distinct(StringComparer.Ordinal).Should().HaveCount(2);
        // The plain dotted name keeps its historical hash-free shape; the ambiguous one is suffixed.
        registrationFiles.Should().Contain("A_B_C_FooHandler.g.cs");
    }

    private static string GenerateHandlerRegistrationSource(string source) {
        var result = GenerateHandlerRegistrationRunResult(source);
        return GetGeneratedSource(result, "Sample_Modules_Sales_Handlers_CreateOrder.g.cs");
    }

    private static GeneratorDriverRunResult GenerateHandlerRegistrationRunResult(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "PipelineHierarchyGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
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

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static string CreateSource(
        string assemblyPipelineAttribute,
        string modulePipelineAttribute,
        string handlerPipelineAttribute) =>
        $$"""
        {{assemblyPipelineAttribute}}

        namespace Elarion.Abstractions {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class UseElarionAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class GenerateModuleHandlersAttribute : System.Attribute;

            public interface IHandler<TRequest, TResponse> {
                System.Threading.Tasks.ValueTask<TResponse> HandleAsync(
                    TRequest request,
                    System.Threading.CancellationToken ct);
            }

            public readonly record struct Result<T>(T Value);
        }

        namespace Elarion.Abstractions.Caching {
            public enum HandlerCacheScope {
                CurrentUser = 0,
                Global = 1,
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class CacheableAttribute(params string[] tags) : System.Attribute {
                public string[] Tags { get; } = tags;
                public int DurationSeconds { get; init; } = 60;
                public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;
                public string[] KeyProperties { get; init; } = [];
            }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class CacheInvalidateAttribute(params string[] tags) : System.Attribute {
                public string[] Tags { get; } = tags;
                public HandlerCacheScope Scope { get; init; } = HandlerCacheScope.CurrentUser;
            }
        }

        namespace Elarion.Abstractions.Resilience {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class ResilientAttribute(string policyName) : System.Attribute {
                public string PolicyName { get; } = policyName;
            }
        }

        namespace Sample.Decorators {
            public sealed class TransactionDecorator<TRequest, TResponse> {
                public TransactionDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }

            public sealed class DbConstraintDecorator<TRequest, TResponse> {
                public DbConstraintDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }

            public sealed class ValidationDecorator<TRequest, TResponse> {
                public ValidationDecorator(Elarion.Abstractions.IHandler<TRequest, TResponse> inner) {
                }
            }
        }

        namespace Elarion.Abstractions.Pipeline {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class DecoratorListAttribute(params System.Type[] decorators) : System.Attribute {
                public System.Type[] Decorators { get; } = decorators;
            }
        }

        namespace Sample.Pipeline {
            [Elarion.Abstractions.Pipeline.DecoratorList(
                typeof(Sample.Decorators.TransactionDecorator<,>),
                typeof(Sample.Decorators.DbConstraintDecorator<,>),
                typeof(Sample.Decorators.ValidationDecorator<,>))]
            [System.AttributeUsage(
                System.AttributeTargets.Assembly | System.AttributeTargets.Class,
                Inherited = false,
                AllowMultiple = false)]
            public sealed class DefaultPipelineAttribute : System.Attribute;

            [Elarion.Abstractions.Pipeline.DecoratorList(typeof(Sample.Decorators.ValidationDecorator<,>))]
            [System.AttributeUsage(
                System.AttributeTargets.Assembly | System.AttributeTargets.Class,
                Inherited = false,
                AllowMultiple = false)]
            public sealed class ReadOnlyPipelineAttribute : System.Attribute;
        }

        namespace Elarion.Abstractions.Modules {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppModuleAttribute(string name) : System.Attribute {
                public string Name { get; } = name;
            }
        }

        namespace Sample.Modules.Sales {
            [Elarion.Abstractions.Modules.AppModule("Sales")]
            {{modulePipelineAttribute}}
            public static partial class SalesModule;
        }

        namespace Sample.Modules.Sales.Handlers {
            {{handlerPipelineAttribute}}
            public sealed class CreateOrder
                : Elarion.Abstractions.IHandler<CreateOrder.Command, Elarion.Abstractions.Result<CreateOrder.Response>> {
                public sealed record Command {
                    public required string CustomerId { get; init; }
                }

                public sealed record Response;

                public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                    Command request,
                    System.Threading.CancellationToken ct) =>
                    default;
            }
        }
        """;
}
