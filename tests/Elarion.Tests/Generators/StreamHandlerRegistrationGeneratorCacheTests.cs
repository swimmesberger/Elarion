using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>Incrementality contract for stream-handler registration: irrelevant edits must not re-emit wiring.</summary>
public sealed class StreamHandlerRegistrationGeneratorCacheTests {
    private const string Source = """
                                  using System.Collections.Generic;
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using Elarion.Abstractions;
                                  using Elarion.Abstractions.Modules;

                                  [assembly: GenerateModuleHandlers]

                                  namespace Sample.App {
                                      [AppModule("App")]
                                      public static class AppModule { }

                                      public sealed record Request(int Id);
                                      public sealed class StreamHandler : IStreamHandler<Request, string> {
                                          public ValueTask<Result<IAsyncEnumerable<string>>> HandleAsync(Request request, CancellationToken ct) =>
                                              ValueTask.FromResult(Result<IAsyncEnumerable<string>>.Success(Values()));
                                          private static async IAsyncEnumerable<string> Values() { yield return "stream"; await Task.Yield(); }
                                      }
                                  }
                                  """;

    [Fact]
    public void ReusesRegistrationOutputsAfterIrrelevantEdit() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new StreamHandlerRegistrationGenerator(),
            Source,
            "StreamHandlerCandidateNodes",
            "StreamHandlerCandidates",
            "StreamHandlers",
            "StreamHandlerModuleAggregation");
    }

    [Fact]
    public void DiscoveryStaysCachedWhenAnUnrelatedFileChanges() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new StreamHandlerRegistrationGenerator(),
            Source,
            "StreamHandlerCandidateNodes",
            "StreamHandlerCandidates");
    }
}
