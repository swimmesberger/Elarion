using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Incrementality contract for <see cref="ElarionManifestGenerator"/> (ADR-0006): every discovery is per-node,
/// diagnostics flow as value-equatable <c>DiagnosticInfo</c> (never raw <c>Diagnostic</c>s, which pin syntax
/// trees), and the fully-built manifest output is value-equatable — so an edit that changes no discovered
/// entry never rebuilds the emitted manifest.
/// </summary>
public sealed class ElarionManifestGeneratorCacheTests {
    private const string Source =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;
        using Elarion.AspNetCore;
        using Microsoft.AspNetCore.Routing;

        namespace Sample.Manifest {
            [AppModule("Manifest")]
            public static class ManifestModule { }

            [ModuleEndpoints("Manifest")]
            public static class ManifestWebEndpoints {
                public static void MapEndpoints(IEndpointRouteBuilder endpoints) { }
            }

            [HttpEndpoint("manifest")]
            [Handler("manifest.get")]
            [RequirePermission("manifests", "read")]
            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                public sealed record Response(string Name);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("manifest"));
            }
        }
        """;

    [Fact]
    public void ReusesOutputsAfterIrrelevantEdit() =>
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ElarionManifestGenerator(),
            Source,
            "ManifestModules",
            "ManifestModuleEndpoints",
            "ManifestHttpEndpoints",
            "ManifestRpcMethods",
            "ManifestResourceFilters",
            "ManifestPermissions",
            "ManifestRoles",
            "ManifestFeatureVariants",
            "ManifestConfigurationVariants",
            "Manifest");

    [Fact]
    public void ManifestStaysCached_WhenAnUnrelatedFileChanges() =>
        // The strict form: every discovery is per-node cached, so the collect + encode + emit stage must not
        // re-run at all for an unrelated-file edit.
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new ElarionManifestGenerator(),
            Source,
            "ManifestModules",
            "ManifestModuleEndpoints",
            "ManifestHttpEndpoints",
            "ManifestRpcMethods",
            "Manifest");
}
