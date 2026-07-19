using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Pipeline;
using Elarion.Tests.Services;
using Xunit;
using Elarion.Pipeline;
using Elarion.Diagnostics;

namespace Elarion.Tests.Features;

public sealed class FeatureGateDecoratorTests {
    private static FeatureGateDecorator<GatedCommand, Result<string>> Decorate(
        Type handlerType, IFeatureFlagService features, RecordingHandler? inner = null) {
        return new FeatureGateDecorator<GatedCommand, Result<string>>(
            inner ?? new RecordingHandler(Result<string>.Success("ok")),
            new HandlerMetadata(handlerType, typeof(GatedCommand), typeof(Result<string>)),
            features);
    }

    [Fact]
    public async Task EnabledFlag_RunsHandler() {
        var inner = new RecordingHandler(Result<string>.Success("ok"));
        var decorator = Decorate(typeof(SingleGateHandler), new FakeFeatureFlags(("new-billing", true)), inner);

        var result = await decorator.HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
        inner.Invoked.Should().BeTrue();
    }

    [Fact]
    public async Task DisabledFlag_ReturnsNotFound_WithoutRunningHandler() {
        var inner = new RecordingHandler(Result<string>.Success("ok"));
        var decorator = Decorate(typeof(SingleGateHandler), new FakeFeatureFlags(("new-billing", false)), inner);

        var result = await decorator.HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        inner.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task NotFoundMessage_DoesNotLeakTheGatedFeatureName() {
        var decorator = Decorate(typeof(SingleGateHandler), new FakeFeatureFlags(("new-billing", false)));

        var result = await decorator.HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Message.Should().NotContain("new-billing");
    }

    [Fact]
    public async Task All_RequiresEveryListedFeature() {
        var partial = await Decorate(typeof(AllGateHandler), new FakeFeatureFlags(("a", true), ("b", false)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        partial.Error.Kind.Should().Be(ErrorKind.NotFound);

        var both = await Decorate(typeof(AllGateHandler), new FakeFeatureFlags(("a", true), ("b", true)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        both.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Any_RequiresAtLeastOneListedFeature() {
        var one = await Decorate(typeof(AnyGateHandler), new FakeFeatureFlags(("a", false), ("b", true)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        one.IsSuccess.Should().BeTrue();

        var none = await Decorate(typeof(AnyGateHandler), new FakeFeatureFlags(("a", false), ("b", false)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        none.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Negate_SatisfiedWhenFeatureIsDisabled() {
        var off = await Decorate(typeof(NegatedGateHandler), new FakeFeatureFlags(("legacy", false)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        off.IsSuccess.Should().BeTrue();

        var on = await Decorate(typeof(NegatedGateHandler), new FakeFeatureFlags(("legacy", true)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        on.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task StackedGates_AreAnded() {
        var partial = await Decorate(typeof(StackedGatesHandler), new FakeFeatureFlags(("a", true), ("b", false)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        partial.Error.Kind.Should().Be(ErrorKind.NotFound);

        var both = await Decorate(typeof(StackedGatesHandler), new FakeFeatureFlags(("a", true), ("b", true)))
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);
        both.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task NoGate_AlwaysRuns_AndNeverQueriesFlags() {
        var flags = new FakeFeatureFlags();
        var result = await Decorate(typeof(NoGateHandler), flags)
            .HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        flags.Queried.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosedGate_TagsHandlerSpanAndRecordsClosedMetric() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        using var handlerActivity = new Activity("handle").Start();
        var decorator = Decorate(typeof(SingleGateHandler), new FakeFeatureFlags(("new-billing", false)));

        var result = await decorator.HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
        handlerActivity.GetTagItem("elarion.feature_gate.outcome").Should().Be("closed");
        meters.Measurements.Should().Contain(m =>
            m.InstrumentName == "handler.feature_gate.closed.count" &&
            m.HasTag("elarion.handler", nameof(SingleGateHandler)));
    }

    [Fact]
    public async Task GateFires_EvenWhenDecoratorIsOutermost() {
        // The decorator reads gates from HandlerMetadata (the true handler type), not inner.GetType(); here `inner`
        // is an unrelated stub, simulating the decorator sitting outermost. The gate must still fire.
        var decorator = Decorate(typeof(SingleGateHandler), new FakeFeatureFlags(("new-billing", false)));

        var result = await decorator.HandleAsync(new GatedCommand(1), TestContext.Current.CancellationToken);

        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    private sealed class FakeFeatureFlags : IFeatureFlagService {
        private readonly Dictionary<string, bool> _flags;

        public FakeFeatureFlags(params (string Name, bool Enabled)[] flags) {
            _flags = flags.ToDictionary(flag => flag.Name, flag => flag.Enabled, StringComparer.Ordinal);
        }

        public List<string> Queried { get; } = [];

        public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) {
            Queried.Add(feature);
            return ValueTask.FromResult(_flags.TryGetValue(feature, out var enabled) && enabled);
        }
    }

    private sealed class RecordingHandler(Result<string> response) : IHandler<GatedCommand, Result<string>> {
        public bool Invoked { get; private set; }

        public ValueTask<Result<string>> HandleAsync(GatedCommand request, CancellationToken ct) {
            Invoked = true;
            return ValueTask.FromResult(response);
        }
    }

    private sealed record GatedCommand(int Id);

    [FeatureGate("new-billing")]
    private sealed class SingleGateHandler;

    [FeatureGate("a", "b")]
    private sealed class AllGateHandler;

    [FeatureGate(FeatureRequirement.Any, "a", "b")]
    private sealed class AnyGateHandler;

    [FeatureGate("legacy", Negate = true)]
    private sealed class NegatedGateHandler;

    [FeatureGate("a")]
    [FeatureGate("b")]
    private sealed class StackedGatesHandler;

    private sealed class NoGateHandler;
}
