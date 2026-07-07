using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class EventConsumerRegistrationGeneratorTests {
    [Fact]
    public void GenerateEventConsumers_DomainSubscriber_EmitsDescriptor() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceCreating(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class InvoiceProjections {
                    [Elarion.Abstractions.Messaging.ConsumeEvent(Order = 3)]
                    public System.Threading.Tasks.ValueTask OnCreating(
                        InvoiceCreating e,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("AddSampleEventConsumers");
        generated.Should().Contain("services.TryAddScoped<global::Sample.Events.InvoiceProjections>();");
        generated.Should().Contain("EventType = typeof(global::Sample.Events.InvoiceCreating)");
        generated.Should().Contain("Plane = global::Elarion.Abstractions.Messaging.EventPlane.Domain");
        generated.Should().Contain("ServiceType = typeof(global::Sample.Events.InvoiceProjections)");
        generated.Should().Contain("Order = 3");
        generated.Should().Contain("InvokeAsync = static (serviceProvider, @event, context, ct) =>");
        generated.Should().Contain("service.OnCreating((global::Sample.Events.InvoiceCreating)@event, ct)");
        generated.Should().NotContain("System.Reflection");
        generated.Should().NotContain("GetCustomAttributes");
    }

    [Fact]
    public void GenerateEventConsumers_VoidSubscriber_ReturnsCompletedTask() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Pinged(string Value) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class PingHandler {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public void OnPing(Pinged e) { }
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("service.OnPing((global::Sample.Events.Pinged)@event);");
        generated.Should().Contain("return global::System.Threading.Tasks.ValueTask.CompletedTask;");
    }

    [Fact]
    public void GenerateEventConsumers_ModuleScoped_EmitsPerModuleMethodAndDefaultServicesFiller() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed record InvoiceCreating(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class InvoiceProjections {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnCreating(
                        InvoiceCreating e,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        var perModule = GetGeneratedSource(result, "BillingEventConsumerExtensions.g.cs");
        perModule.Should().Contain(
            "public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddBillingEventConsumers(");
        perModule.Should().Contain("EventType = typeof(global::Sample.Events.InvoiceCreating)");

        var anyTree = string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));
        anyTree.Should().Contain(
            "global::Sample.Events.BillingEventConsumerExtensions.AddBillingEventConsumers(services);");
        anyTree.Should().Contain(
            "static partial void AddEventConsumers(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    [Fact]
    public void GenerateEventConsumers_UnmatchedModule_EmitsWarning() {
        var source = CreateSource(
            """
            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace Other.Events {
                public sealed record Stray(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class StrayConsumer {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnStray(Stray e) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELEVT003" && d.Severity == DiagnosticSeverity.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_MatchedModule_DoesNotWarnUnmatched() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed record InvoiceCreating(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class InvoiceProjections {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnCreating(InvoiceCreating e) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELEVT003").Should().BeFalse();
    }

    [Fact]
    public void GenerateEventConsumers_NoModules_DoesNotWarnUnmatched() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Stray(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class StrayConsumer {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnStray(Stray e) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """,
            wrapInModule: false);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELEVT003").Should().BeFalse();
    }

    [Fact]
    public void GenerateEventConsumers_IntegrationSubscriber_EmitsIntegrationPlane() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceCreated(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Abstractions.Service]
                public sealed class EmailSender {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnCreated(
                        InvoiceCreated e,
                        Elarion.Abstractions.Messaging.IEventContext context) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("Plane = global::Elarion.Abstractions.Messaging.EventPlane.Integration");
        generated.Should().Contain("return new global::System.Threading.Tasks.ValueTask(service.OnCreated((global::Sample.Events.InvoiceCreated)@event, context));");
    }

    [Fact]
    public void GenerateEventConsumers_GenericContext_EmitsTypedCast() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Tick(int N) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class TickHandler {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnTick(
                        Tick e,
                        Elarion.Abstractions.Messaging.IEventContext<Tick> context) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "service.OnTick((global::Sample.Events.Tick)@event, (global::Elarion.Abstractions.Messaging.IEventContext<global::Sample.Events.Tick>)context)");
    }

    [Fact]
    public void GenerateEventConsumers_UseElarion_EmitsDescriptor() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Boom(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class BoomHandler {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnBoom(Boom e) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """,
            "[assembly: Elarion.Abstractions.UseElarion]");

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("EventType = typeof(global::Sample.Events.Boom)");
    }

    [Fact]
    public void GenerateEventConsumers_ConsumerNotOnService_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Orphan(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                public sealed class NotAService {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnOrphan(Orphan e) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT001"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT001" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_InvalidSignature_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record A(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;
                public sealed record B(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class TwoEvents {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask OnBoth(A a, B b) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT002"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_MethodForm_GenericResultReturn_EmitsDiagnostic() {
        // A Result<T> with a VALUE is request/reply, rejected on either plane — including domain, which used to
        // be the responder role (ADR-0010). The non-generic Result is fine (see the test below).
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record GetTotal(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class BadResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public Elarion.Abstractions.Result<int> Answer(GetTotal request) =>
                        Elarion.Abstractions.Result<int>.Success(1);
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT002"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_MethodForm_NonGenericResult_EmitsFailingSubscriber() {
        // A method-form consumer may return the non-generic Result (no value, success/failure) — a failed
        // Result surfaces as EventConsumerFailedException, the same failure channel as the handler form.
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Shipped(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class ShipmentProjections {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> OnShipped(
                        Shipped e,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
        generated.Should().Contain(
            "var result = await service.OnShipped((global::Sample.Events.Shipped)@event, ct).ConfigureAwait(false);");
        generated.Should().Contain(
            "throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
    }

    [Fact]
    public void GenerateEventConsumers_HandlerForm_DomainEvent_EmitsHandlerInvoker() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceCreated(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Messaging.ConsumeEvent(Order = 2)]
                public sealed class ProjectInvoice : Elarion.Abstractions.IHandler<InvoiceCreated> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        InvoiceCreated request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("EventType = typeof(global::Sample.Events.InvoiceCreated)");
        generated.Should().Contain("Plane = global::Elarion.Abstractions.Messaging.EventPlane.Domain");
        generated.Should().Contain("Order = 2");
        generated.Should().Contain(
            "ServiceType = typeof(global::Elarion.Abstractions.IHandler<global::Sample.Events.InvoiceCreated, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>)");
        generated.Should().Contain("InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
        // Resolved keyed by the consumer's FQN so multiple consumers of one event resolve distinctly (ADR-0046).
        generated.Should().Contain(
            "var handler = serviceProvider.GetRequiredKeyedService<global::Elarion.Abstractions.IHandler<global::Sample.Events.InvoiceCreated, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>>(\"global::Sample.Events.ProjectInvoice\");");
        generated.Should().Contain(
            "var result = await handler.HandleAsync((global::Sample.Events.InvoiceCreated)@event, ct).ConfigureAwait(false);");
        generated.Should().Contain(
            "throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
        // The handler is registered by the handler generator, not re-registered here.
        generated.Should().NotContain("TryAddScoped<global::Elarion.Abstractions.IHandler");
        generated.Should().NotContain("TryAddScoped<global::Sample.Events.ProjectInvoice>");
    }

    [Fact]
    public void GenerateEventConsumers_TwoHandlerFormConsumersOfSameEvent_ResolveDistinctlyByKey() {
        // Regression: two handler-form consumers of one event used to collide on the shared
        // IHandler<TEvent, Result<Unit>> registration (GetRequiredService returned the last, so one silently
        // never ran). Each is now registered and resolved keyed by its own FQN (ADR-0046).
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceCreated(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class NotifyBilling : Elarion.Abstractions.IHandler<InvoiceCreated> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        InvoiceCreated request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }

                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class ReindexInvoice : Elarion.Abstractions.IHandler<InvoiceCreated> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        InvoiceCreated request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        // Both descriptors resolve their own consumer, keyed by FQN — neither shadows the other. (The keyed
        // registration side is emitted by the handler generator; see HandlerRegistrationGeneratorTests.)
        generated.Should().Contain(
            "serviceProvider.GetRequiredKeyedService<global::Elarion.Abstractions.IHandler<global::Sample.Events.InvoiceCreated, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>>(\"global::Sample.Events.NotifyBilling\");");
        generated.Should().Contain(
            "serviceProvider.GetRequiredKeyedService<global::Elarion.Abstractions.IHandler<global::Sample.Events.InvoiceCreated, global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>>>(\"global::Sample.Events.ReindexInvoice\");");
    }

    [Fact]
    public void GenerateEventConsumers_HandlerForm_IntegrationEvent_EmitsIntegrationPlane() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceShipped(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class NotifyShipment : Elarion.Abstractions.IHandler<InvoiceShipped> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result> HandleAsync(
                        InvoiceShipped request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result.Success());
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("Plane = global::Elarion.Abstractions.Messaging.EventPlane.Integration");
        generated.Should().Contain("InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
    }

    [Fact]
    public void GenerateEventConsumers_HandlerForm_DomainTypedResponse_EmitsDiagnostic() {
        // A handler-form consumer returning a non-Unit Result<T> (request/reply) is rejected on the domain
        // plane too — it used to be the single responder, a role ADR-0010 removed.
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Recompute(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class RecomputeTotals
                    : Elarion.Abstractions.IHandler<Recompute, Elarion.Abstractions.Result<int>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<int>> HandleAsync(
                        Recompute request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result<int>.Success(0));
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT005"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT005" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_HandlerForm_IntegrationTypedResponse_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record Notified(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class BadIntegrationResponder
                    : Elarion.Abstractions.IHandler<Notified, Elarion.Abstractions.Result<int>> {
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<int>> HandleAsync(
                        Notified request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result<int>.Success(0));
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT005"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT005" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_HandlerForm_NotAHandler_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class NotAHandler { }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT005"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT005" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_IrrelevantEdit_ReusesPipeline() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record InvoiceCreating(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class InvoiceProjections {
                    [Elarion.Abstractions.Messaging.ConsumeEvent(Order = 3)]
                    public System.Threading.Tasks.ValueTask OnCreating(
                        InvoiceCreating e,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new EventConsumerRegistrationGenerator(),
            source,
            "EventConsumers",
            "EventConsumersCombined");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateEventConsumers]",
        bool wrapInModule = true)
    {
        // Consumers register only per module, so wrap the `Sample.Events` test namespace in a module by default.
        // Skip when the test already declares a module; tests asserting no-module behavior pass false.
        var moduleDeclaration = wrapInModule && !testSource.Contains("AppModule(")
            ? """
            namespace Sample.Events {
                [Elarion.Abstractions.Modules.AppModule("Sample")]
                public static class GeneratedTestModule { }
            }
            """
            : "";

        return $"""
        {assemblyTrigger}

        {moduleDeclaration}

        {testSource}
        """;
    }

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));

    private static GeneratorDriverRunResult Generate(
        string source,
        bool assertGeneratedOutputCompiles = true,
        string[]? allowedDiagnosticIds = null) {
        var allowedIds = new HashSet<string>(allowedDiagnosticIds ?? []);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "EventConsumerRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new EventConsumerRegistrationGenerator(), new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);
        var result = driver.GetRunResult();

        generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error && !allowedIds.Contains(d.Id))
            .Should().BeEmpty();

        if (assertGeneratedOutputCompiles) {
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty();
        }

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
}
