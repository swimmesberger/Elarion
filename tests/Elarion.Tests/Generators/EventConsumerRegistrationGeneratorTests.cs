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
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

        generated.Should().Contain("AddEventConsumerRegistrationGeneratorTestsEventConsumers");
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
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

        generated.Should().Contain("service.OnPing((global::Sample.Events.Pinged)@event);");
        generated.Should().Contain("return global::System.Threading.Tasks.ValueTask.CompletedTask;");
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
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

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
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

        generated.Should().Contain(
            "service.OnTick((global::Sample.Events.Tick)@event, (global::Elarion.Abstractions.Messaging.IEventContext<global::Sample.Events.Tick>)context)");
    }

    [Fact]
    public void GenerateEventConsumers_SyncResponder_EmitsRequestDelegate() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record GetTotal(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class TotalsResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public Elarion.Abstractions.Result<int> Answer(GetTotal request) =>
                        Elarion.Abstractions.Result<int>.Success(42);
                }
            }
            """);

        var result = Generate(source);
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

        generated.Should().Contain("ResponseType = typeof(int)");
        generated.Should().Contain("InvokeRequestAsync = static (serviceProvider, request, context, ct) =>");
        generated.Should().Contain(
            "return new global::System.Threading.Tasks.ValueTask<object>((object)service.Answer((global::Sample.Events.GetTotal)request));");
    }

    [Fact]
    public void GenerateEventConsumers_AsyncResponder_EmitsAwaitingDelegate() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record GetName(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class NameResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<string>> Answer(
                        GetName request,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult(Elarion.Abstractions.Result<string>.Success("x"));
                }
            }
            """);

        var result = Generate(source);
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

        generated.Should().Contain("ResponseType = typeof(string)");
        generated.Should().Contain("InvokeRequestAsync = static async (serviceProvider, request, context, ct) =>");
        generated.Should().Contain("return (object)await service.Answer((global::Sample.Events.GetName)request, ct);");
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
        var generated = GetGeneratedSource(result, "EventConsumerRegistration.g.cs");

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
    public void GenerateEventConsumers_IntegrationResponder_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record AskIntegration(int Id) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Abstractions.Service]
                public sealed class BadResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public Elarion.Abstractions.Result<int> Answer(AskIntegration request) =>
                        Elarion.Abstractions.Result<int>.Success(1);
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT002"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateEventConsumers_DuplicateResponder_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Events {
                public sealed record GetValue(int Id) : Elarion.Abstractions.Messaging.IDomainEvent;

                [Elarion.Abstractions.Service]
                public sealed class FirstResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public Elarion.Abstractions.Result<int> Answer(GetValue request) =>
                        Elarion.Abstractions.Result<int>.Success(1);
                }

                [Elarion.Abstractions.Service]
                public sealed class SecondResponder {
                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public Elarion.Abstractions.Result<int> Answer(GetValue request) =>
                        Elarion.Abstractions.Result<int>.Success(2);
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELEVT004"]);

        result.Diagnostics.Any(d => d.Id == "ELEVT004" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateEventConsumers]") =>
        $"""
        {assemblyTrigger}

        {testSource}
        """;

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

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new EventConsumerRegistrationGenerator())
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
