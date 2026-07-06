using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ActorRegistrationGeneratorTests {
    [Fact]
    public void GenerateActors_KeyedActor_EmitsFacadeWorkItemsAndRegistration() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record ShipmentInfo(string Street);

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    private int _shipments;

                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task<int> Ship(
                        ShipmentInfo info,
                        System.Threading.CancellationToken cancellationToken) {
                        _shipments++;
                        return System.Threading.Tasks.Task.FromResult(_shipments);
                    }

                    public System.Threading.Tasks.ValueTask Reset() {
                        _shipments = 0;
                        return System.Threading.Tasks.ValueTask.CompletedTask;
                    }
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain(
            "public interface IOrderFulfillment : global::Elarion.Actors.IActorFacade<global::System.Guid>");
        generated.Should().Contain(
            "global::System.Threading.Tasks.Task<int> Ship(global::Sample.Orders.ShipmentInfo info, global::System.Threading.CancellationToken cancellationToken = default);");
        generated.Should().Contain("internal sealed class OrderFulfillmentActorFacade : IOrderFulfillment");
        // Facade methods are pass-through (no async wrapper): Task shapes bridge via AsTask(),
        // ValueTask shapes return the handle's ValueTask directly (ADR-0042 call-path roadmap).
        generated.Should().Contain("_handle.InvokeAsync(new ShipWorkItem(info), cancellationToken).AsTask();");
        generated.Should().Contain("_handle.InvokeAsync(new ResetWorkItem(), cancellationToken);");
        generated.Should().NotContain("public async");
        generated.Should().Contain(
            "private sealed class ShipWorkItem : global::Elarion.Actors.ActorWorkItem<global::Sample.Orders.OrderFulfillmentActor, int>");
        generated.Should().Contain("return await actor.Ship(_info, cancellationToken).ConfigureAwait(false);");
        // The void-shaped method rides a Unit work item.
        generated.Should().Contain("global::Elarion.Abstractions.Results.Unit");
        generated.Should().Contain("AddSampleActors");
        generated.Should().Contain(
            "new global::Elarion.Actors.ActorRegistration<global::Sample.Orders.OrderFulfillmentActor, global::System.Guid, global::Sample.Orders.IOrderFulfillment>");
        generated.Should().Contain("Name = \"OrderFulfillment\"");
        generated.Should().Contain("Activator = static (serviceProvider, context) => new global::Sample.Orders.OrderFulfillmentActor(context)");
        generated.Should().Contain("Facade = static handle => new global::Sample.Orders.OrderFulfillmentActorFacade(handle)");
        generated.Should().NotContain("System.Reflection");
    }

    [Fact]
    public void GenerateActors_ModuleScoped_EmitsDefaultServicesFiller() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class PingActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("global::Sample.Orders.SampleActorExtensions.AddSampleActors(services);");
        generated.Should().Contain(
            "static partial void AddActors(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    [Fact]
    public void GenerateActors_SingletonActor_UsesSingletonKeyAndDependencyInjection() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public interface IClock { }

                [Elarion.Actors.Actor(Name = "Wallet")]
                public sealed class WalletActor {
                    public WalletActor(IClock clock) { }

                    public System.Threading.Tasks.Task<decimal> Balance() =>
                        System.Threading.Tasks.Task.FromResult(0m);
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("public interface IWallet : global::Elarion.Actors.IActorFacade");
        generated.Should().NotContain("IActorFacade<global::Sample.Orders");
        generated.Should().Contain(
            "new global::Elarion.Actors.ActorRegistration<global::Sample.Orders.WalletActor, global::Elarion.Actors.ActorSingletonKey, global::Sample.Orders.IWallet>");
        generated.Should().Contain("serviceProvider.GetRequiredService<global::Sample.Orders.IClock>()");
    }

    [Fact]
    public void GenerateActors_AttributeKnobs_MapToOptions() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(
                    MailboxCapacity = 128,
                    MailboxFullMode = Elarion.Actors.ActorMailboxFullMode.Fail,
                    IdleTimeoutSeconds = -1,
                    CallTimeoutSeconds = 2.5)]
                [Elarion.Actors.Reentrant]
                public sealed class TunedActor {
                    public System.Threading.Tasks.Task Touch() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generated = AllGenerated(result);

        generated.Should().Contain("MailboxCapacity = 128");
        generated.Should().Contain("MailboxFullMode = global::Elarion.Actors.ActorMailboxFullMode.Fail");
        generated.Should().Contain("IdleTimeout = null");
        generated.Should().Contain("CallTimeout = global::System.TimeSpan.FromSeconds(2.5)");
        generated.Should().Contain("Reentrant = true");
    }

    [Fact]
    public void GenerateActors_ActorOutsideModule_EmitsWarning() {
        var source = CreateSource(
            """
            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace Other.Place {
                [Elarion.Actors.Actor]
                public sealed class StrayActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            wrapInModule: false);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELACT003" && d.Severity == DiagnosticSeverity.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_SynchronousMethod_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class BrokenActor {
                    public int Count() => 0;
                }
            }
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT002"]);

        result.Diagnostics.Any(d => d.Id == "ELACT002" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConflictingKeyDeclarations_EmitsDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor(KeyType = typeof(string))]
                public sealed class ConflictedActor {
                    public ConflictedActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELACT004"]);

        result.Diagnostics.Any(d => d.Id == "ELACT004" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnActorMethod_EmitsGuidanceDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                public sealed record OrderShipped(System.Guid OrderId) : Elarion.Abstractions.Messaging.IIntegrationEvent;

                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    [Elarion.Abstractions.Messaging.ConsumeEvent]
                    public System.Threading.Tasks.Task OnShipped(OrderShipped e) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT007"]);

        result.Diagnostics.Any(d => d.Id == "ELACT007" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
        // The event-consumer generator yields to ELACT007 instead of reporting a misleading
        // "not a [Service]" for the same method.
        result.Diagnostics.Should().NotContain(d => d.Id == "ELEVT001" || d.Id == "ELEVT005");
    }

    [Fact]
    public void GenerateActors_ConsumeEventOnActorClass_EmitsGuidanceDiagnostic() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                [Elarion.Abstractions.Messaging.ConsumeEvent]
                public sealed class OrderFulfillmentActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: """
            [assembly: Elarion.Abstractions.GenerateActors]
            [assembly: Elarion.Abstractions.GenerateEventConsumers]
            """);

        var result = Generate(source, allowedDiagnosticIds: ["ELACT007"]);

        result.Diagnostics.Any(d => d.Id == "ELACT007" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
        result.Diagnostics.Should().NotContain(d => d.Id == "ELEVT001" || d.Id == "ELEVT005");
    }

    [Fact]
    public void GenerateActors_NoTrigger_EmitsNothing() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class PingActor {
                    public System.Threading.Tasks.Task Ping() => System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """,
            assemblyTrigger: "");

        var result = Generate(source);

        AllGenerated(result).Should().NotContain("IPing");
    }

    [Fact]
    public void GenerateActors_IrrelevantEdit_ReusesPipeline() {
        var source = CreateSource(
            """
            namespace Sample.Orders {
                [Elarion.Actors.Actor]
                public sealed class OrderFulfillmentActor {
                    public OrderFulfillmentActor(Elarion.Actors.IActorContext<System.Guid> context) { }

                    public System.Threading.Tasks.Task<int> Count() =>
                        System.Threading.Tasks.Task.FromResult(0);
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ActorRegistrationGenerator(),
            source,
            "Actors",
            "ActorsCombined");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateActors]",
        bool wrapInModule = true) {
        // Actors register only per module, so wrap the `Sample.Orders` test namespace in a module by
        // default. Tests asserting no-module behavior pass false.
        var moduleDeclaration = wrapInModule && !testSource.Contains("AppModule(")
            ? """
            namespace Sample.Orders {
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
            "ActorRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                new ActorRegistrationGenerator(),
                new EventConsumerRegistrationGenerator(),
                new ModuleDefaultServicesGenerator())
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

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
