using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Xunit;

using Elarion.Pipeline;
using Elarion.Diagnostics;
namespace Elarion.Tests.Services;

public sealed class TracingDecoratorTests {
    private static readonly HandlerMetadata Metadata =
        new(typeof(Request), typeof(Request), typeof(Result<int>));

    [Fact]
    public async Task HandleAsync_SuccessResult_EmitsSpanAndMetricWithOkOutcome() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var decorator = new TracingDecorator<Request, Result<int>>(
            new SuccessHandler(), "SuccessHandler", Metadata);

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
        var decorator = new TracingDecorator<Request, Result<int>>(
            new FailureHandler(), "FailureHandler", Metadata);

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
        var decorator = new TracingDecorator<Request, Result<int>>(
            new ThrowingHandler(), "ThrowingHandler", Metadata);

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
        var decorator = new TracingDecorator<Request, Result<int>>(
            new SuccessHandler(), "MetricOnlyHandler", Metadata);

        await decorator.HandleAsync(new Request(), TestContext.Current.CancellationToken);

        meters.Measurements.Should().Contain(measurement =>
            measurement.InstrumentName == "handler.execution.count" &&
            measurement.HasTag("elarion.handler", "MetricOnlyHandler") &&
            measurement.HasTag("elarion.handler.outcome", "ok"));
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
