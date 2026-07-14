using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Xunit;

using Elarion.Pipeline;
using Elarion.Diagnostics;
namespace Elarion.Tests.Services;

// The tracing + execution-metric half of the merged ObservabilityDecorator (ADR-0059). The context-enrichment
// half is covered by HandlerObservabilityEnrichmentTests.
public sealed class ObservabilityDecoratorTests {
    private static readonly HandlerMetadata Metadata =
        new(typeof(Request), typeof(Request), typeof(Result<int>));

    private static readonly IHandlerContextEnricher[] NoEnrichers = [];

    private static ObservabilityDecorator<Request, Result<int>> Decorate(
        IHandler<Request, Result<int>> inner, string name, HandlerMetadata? metadata = null) =>
        new(inner, name, metadata ?? Metadata, NoEnrichers, loggerFactory: null);

    [Fact]
    public async Task HandleAsync_SuccessResult_EmitsSpanAndMetricWithOkOutcome() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = Decorate(new SuccessHandler(), "SuccessHandler");

        var result = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handle SuccessHandler" &&
            activity.Kind == ActivityKind.Internal &&
            Equals(activity.GetTag("elarion.handler"), "SuccessHandler") &&
            Equals(activity.GetTag("elarion.handler.request_type"), nameof(Request)) &&
            Equals(activity.GetTag("elarion.handler.outcome"), "ok") &&
            activity.Status != ActivityStatusCode.Error);
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler", "SuccessHandler") &&
            measurement.HasTag("elarion.handler.outcome", "ok"));
    }

    [Fact]
    public async Task HandleAsync_FailureResult_RecordsErrorOutcome() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = Decorate(new FailureHandler(), "FailureHandler");

        var result = await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handle FailureHandler" &&
            Equals(activity.GetTag("elarion.handler.outcome"), "error") &&
            activity.Status == ActivityStatusCode.Error);
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler", "FailureHandler") &&
            measurement.HasTag("elarion.handler.outcome", "error"));
    }

    [Fact]
    public async Task HandleAsync_InnerThrows_RecordsExceptionAndRethrows() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = Decorate(new ThrowingHandler(), "ThrowingHandler");

        var act = async () => await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        activities.Activities.Should().Contain(activity =>
            activity.DisplayName == "handle ThrowingHandler" &&
            Equals(activity.GetTag("elarion.handler.outcome"), "exception") &&
            activity.Status == ActivityStatusCode.Error &&
            activity.Events.Any(evt => evt.Name == "exception"));
        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler", "ThrowingHandler") &&
            measurement.HasTag("elarion.handler.outcome", "exception"));
    }

    [Fact]
    public async Task HandleAsync_NoActivityListener_StillRecordsMetric() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = Decorate(new SuccessHandler(), "MetricOnlyHandler");

        await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler", "MetricOnlyHandler") &&
            measurement.HasTag("elarion.handler.outcome", "ok"));
    }

    [Fact]
    public async Task HandleAsync_TwoHandlersSharingRequestAndResponse_ReportTheirOwnPipelineTags() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        // Two handlers with the same TRequest/TResponse (e.g. two handler-form consumers of one event) share
        // the closed ObservabilityDecorator<,> generic but have different metadata pipelines — each span
        // must carry its OWN pipeline, not whichever handler rendered first.
        var metadataA = new HandlerMetadata(
            typeof(SuccessHandler), typeof(Request), typeof(Result<int>),
            () => [new PipelineStep(typeof(ObservabilityDecorator<,>), Conditional: false),
                new PipelineStep(typeof(AuthorizationDecorator<,>), Conditional: false)]);
        var metadataB = new HandlerMetadata(
            typeof(FailureHandler), typeof(Request), typeof(Result<int>),
            () => [new PipelineStep(typeof(ObservabilityDecorator<,>), Conditional: false),
                new PipelineStep(typeof(TransactionDecorator<,>), Conditional: true)]);
        var decoratorA = Decorate(new SuccessHandler(), "HandlerA", metadataA);
        var decoratorB = Decorate(new SuccessHandler(), "HandlerB", metadataB);

        await decoratorA.HandleAsync(new Request(), TestContext.Current.CancellationToken);
        await decoratorB.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        activities.Activities.Should().Contain(activity =>
            Equals(activity.GetTag("elarion.handler"), "HandlerA") &&
            Equals(activity.GetTag("elarion.handler.pipeline"), "Observability,Authorization"));
        activities.Activities.Should().Contain(activity =>
            Equals(activity.GetTag("elarion.handler"), "HandlerB") &&
            Equals(activity.GetTag("elarion.handler.pipeline"), "Observability,Transaction?"));
    }

    private sealed record Request;

    private sealed class SuccessHandler : IHandler<Request, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(7));
    }

    private sealed class FailureHandler : IHandler<Request, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Failure(AppError.NotFound("missing")));
    }

    private sealed class ThrowingHandler : IHandler<Request, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Request request, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
