using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Incrementality contract for <see cref="HandlerRegistrationGenerator"/> (ADR-0006): the per-node candidate
/// stage must stay cached for untouched files, and an irrelevant edit must never re-emit.
/// </summary>
public sealed class HandlerRegistrationGeneratorCacheTests {
    private const string Source =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]

        namespace Sample.App {
            [AppModule("App")]
            public static class AppModule { }

            public sealed record PingCommand(int Id) : ICommand;
            public sealed record PingResponse(string Name);

            public sealed class PingHandler : IHandler<PingCommand, Result<PingResponse>> {
                public ValueTask<Result<PingResponse>> HandleAsync(PingCommand request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<PingResponse>.Success(new PingResponse("pong")));
            }

            public interface IPricingService { }

            // A [Service] with a declared scope keeps the ServiceScopeFacts provider (ELSG011/012 input,
            // ADR-0066) populated so the cache assertions exercise it.
            [Service(typeof(IPricingService), Scope = ServiceScope.Singleton)]
            public sealed class PricingService : IPricingService { }
        }
        """;

    [Fact]
    public void ReusesOutputsAfterIrrelevantEdit() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(),
            Source,
            "HandlerCandidateNodes",
            "HandlerCandidates",
            "VariantContracts",
            "NoRetryPolicies",
            "ServiceScopeFacts",
            "Handlers",
            "HandlerModuleAggregation");
    }

    [Fact]
    public void DiscoveryStaysCached_WhenAnUnrelatedFileChanges() {
        // The strict form: candidate discovery is per-node, so editing another file must not re-bind the
        // handler's file at all (reason Cached, not merely Unchanged). The "Handlers" resolution stage is
        // deliberately excluded — it combines the compilation to keep cross-file [DecoratorList]/[Require*]
        // state fresh, re-running (cheaply, over known handlers only) with an equal, emission-cached output.
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new HandlerRegistrationGenerator(),
            Source,
            "HandlerCandidateNodes",
            "HandlerCandidates",
            "VariantContracts",
            "NoRetryPolicies",
            "ServiceScopeFacts");
    }
}
