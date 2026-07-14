using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using Elarion.Pipeline;
using Elarion.Tests.Services;   // ActivityCollector (internal, same assembly)
using Xunit;

namespace Elarion.Tests.Pipeline;

public sealed class HandlerPipelineTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A request type local to this class, so ObservabilityDecorator<PipeReq, Result<int>>'s per-type
    // rendered-tag cache is independent of the one in the Services observability-decorator tests.
    private sealed record PipeReq : ICommand;

    [Fact]
    public void Pipeline_DefaultsToEmpty_WhenNoAccessor() {
        var meta = new HandlerMetadata(typeof(PipeReq), typeof(PipeReq), typeof(Result<int>));

        meta.Pipeline.Steps.Should().BeEmpty();
        meta.Pipeline.Contains(typeof(ObservabilityDecorator<,>)).Should().BeFalse();
    }

    [Fact]
    public void Pipeline_IsLateBound_EmptyUntilResolved_ThenReflectsTheCache() {
        IReadOnlyList<PipelineStep>? cache = null;
        var meta = new HandlerMetadata(
            typeof(PipeReq), typeof(PipeReq), typeof(Result<int>), () => cache ?? []);

        // Before "resolution" the accessor returns empty — the documented lifecycle.
        meta.Pipeline.Steps.Should().BeEmpty();

        cache = new[] {
            new PipelineStep(typeof(ObservabilityDecorator<,>), Conditional: false),
            new PipelineStep(typeof(AuditDecorator<,>), Conditional: true),
        };

        meta.Pipeline.Steps.Should().HaveCount(2);
        meta.Pipeline.Steps[0].Decorator.Should().Be(typeof(ObservabilityDecorator<,>));
        meta.Pipeline.Steps[0].Conditional.Should().BeFalse();
        meta.Pipeline.Steps[1].Conditional.Should().BeTrue();
        meta.Pipeline.Contains(typeof(AuditDecorator<,>)).Should().BeTrue();
        meta.Pipeline.Contains(typeof(CacheDecorator<,>)).Should().BeFalse();
    }

    [Fact]
    public async Task ObservabilityDecorator_RendersPipelineTag_InExecutionOrder_MarkingConditionalSteps() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var meta = new HandlerMetadata(typeof(PipeReq), typeof(PipeReq), typeof(Result<int>), () => new[] {
            new PipelineStep(typeof(ObservabilityDecorator<,>), Conditional: false),
            new PipelineStep(typeof(AuthorizationDecorator<,>), Conditional: false),
            new PipelineStep(typeof(AuditDecorator<,>), Conditional: true),   // soft-attached → trailing '?'
        });
        var decorator = new ObservabilityDecorator<PipeReq, Result<int>>(new PipeHandler(), "Pipe", meta, [], null);

        await decorator.HandleAsync(new PipeReq(), Ct);

        activities.Activities.Should().Contain(activity =>
            Equals(activity.GetTag("elarion.handler.pipeline"), "Observability,Authorization,Audit?"));
    }

    private sealed class PipeHandler : IHandler<PipeReq, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(PipeReq request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(1));
    }
}
