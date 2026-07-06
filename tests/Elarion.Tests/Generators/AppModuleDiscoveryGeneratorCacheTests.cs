using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Incrementality contract for <see cref="AppModuleDiscoveryGenerator"/> (ADR-0006): module discovery is
/// per-node, and the fully-built bootstrapper output is value-equatable — so an edit that changes no module,
/// manifest, trigger, or sibling probe never rebuilds (or re-parses) the emitted bootstrapper.
/// </summary>
public sealed class AppModuleDiscoveryGeneratorCacheTests {
    private const string Source =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;
        using Elarion.AspNetCore;
        using Microsoft.AspNetCore.Routing;

        [assembly: GenerateModuleBootstrapper]

        namespace Sample.App {
            [AppModule("App")]
            public static class AppModule { }

            [ModuleEndpoints("App")]
            public static class AppWebEndpoints {
                public static void MapEndpoints(IEndpointRouteBuilder endpoints) { }
            }

            public sealed record PingCommand(int Id) : ICommand;
            public sealed record PingResponse(string Name);

            [Handler("app.ping")]
            public sealed class PingHandler : IHandler<PingCommand, Result<PingResponse>> {
                public ValueTask<Result<PingResponse>> HandleAsync(PingCommand request, CancellationToken ct) =>
                    ValueTask.FromResult(Result<PingResponse>.Success(new PingResponse("pong")));
            }
        }
        """;

    [Fact]
    public void ReusesOutputsAfterIrrelevantEdit() =>
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new AppModuleDiscoveryGenerator(),
            Source,
            "BootstrapperModules",
            "BootstrapperModuleEndpoints",
            "BootstrapperSiblings",
            "Bootstrapper");

    [Fact]
    public void BootstrapperStaysCached_WhenAnUnrelatedFileChanges() =>
        // The strict form: every input of the final model either is per-node cached (modules, manifests,
        // [ModuleEndpoints] contributors) or projects to an equal small value (trigger, sibling probes, root
        // namespace), so the expensive collect + topological-sort + BuildSource stage must not re-run at all
        // for an unrelated-file edit.
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new AppModuleDiscoveryGenerator(),
            Source,
            "BootstrapperModules",
            "BootstrapperModuleEndpoints",
            "Bootstrapper");
}
