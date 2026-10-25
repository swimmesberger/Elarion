using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class UnexpectedErrorOptionsTests {
    private static UnexpectedErrorContext Context(IServiceProvider services, string? operation = "orders.create") {
        return new UnexpectedErrorContext {
            Exception = new InvalidOperationException("secret detail"),
            Transport = UnexpectedErrorTransports.JsonRpc,
            Operation = operation,
            Services = services
        };
    }

    [Fact]
    public void Defaults_AnswerTheGenericMessageAndTheCurrentTraceId() {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var error = UnexpectedErrorOptions.Describe(Context(services));

        error.Message.Should().Be(UnexpectedErrorOptions.DefaultMessage);
        error.Reference.Should().Be(activity.TraceId.ToHexString());
    }

    [Fact]
    public void Defaults_WithoutATrace_StillAnswerAReference() {
        using var services = new ServiceCollection().BuildServiceProvider();
        var previous = Activity.Current;
        Activity.Current = null;
        try {
            var first = UnexpectedErrorOptions.Describe(Context(services));
            var second = UnexpectedErrorOptions.Describe(Context(services));

            first.Reference.Should().NotBeNullOrWhiteSpace();
            second.Reference.Should().NotBe(first.Reference);
        }
        finally {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void ConfiguredHooks_ReceiveTheContextAndAccumulateAcrossCalls() {
        UnexpectedErrorContext? seen = null;
        using var services = new ServiceCollection()
            .ConfigureElarionUnexpectedErrors(o => o.Reference = _ => "first")
            .ConfigureElarionUnexpectedErrors(o => o.Message = context => {
                seen = context;
                return $"Failed: {context.Operation}";
            })
            .BuildServiceProvider();
        var context = Context(services);

        var error = UnexpectedErrorOptions.Describe(context);

        error.Message.Should().Be("Failed: orders.create");
        error.Reference.Should().Be("first", "a later call overrides only the hooks it sets");
        seen.Should().BeSameAs(context);
    }

    [Fact]
    public void ReferenceHookReturningNull_SendsNoReference() {
        using var services = new ServiceCollection()
            .ConfigureElarionUnexpectedErrors(o => o.Reference = _ => null)
            .BuildServiceProvider();

        UnexpectedErrorOptions.Describe(Context(services)).Reference.Should().BeNull();
    }

    [Fact]
    public void ThrowingHooks_FallBackToTheDefaults() {
        using var services = new ServiceCollection()
            .ConfigureElarionUnexpectedErrors(o => {
                o.Message = _ => throw new InvalidOperationException("localizer broke");
                o.Reference = _ => throw new InvalidOperationException("reference broke");
            })
            .BuildServiceProvider();

        var error = UnexpectedErrorOptions.Describe(Context(services));

        error.Message.Should().Be(UnexpectedErrorOptions.DefaultMessage);
        error.Reference.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ThrowingConfiguration_StillAnswersTheDefaults_AndAnswersInsteadOfLeavingToTheHost() {
        using var services = new ServiceCollection()
            .ConfigureElarionUnexpectedErrors(_ => throw new InvalidOperationException("configuration broke"))
            .BuildServiceProvider();

        var error = UnexpectedErrorOptions.Describe(Context(services));

        error.Message.Should().Be(UnexpectedErrorOptions.DefaultMessage);
        error.Reference.Should().NotBeNullOrWhiteSpace();
        UnexpectedErrorOptions.LeavesToHost(Context(services)).Should().BeFalse();
    }

    [Fact]
    public void LeaveToHost_DefaultsToAnswering_AndFollowsTheHook() {
        using var none = new ServiceCollection().BuildServiceProvider();
        using var configured = new ServiceCollection()
            .ConfigureElarionUnexpectedErrors(o => o.LeaveToHost = context => context.Exception is InvalidOperationException)
            .BuildServiceProvider();

        UnexpectedErrorOptions.LeavesToHost(Context(none)).Should().BeFalse();
        UnexpectedErrorOptions.LeavesToHost(Context(configured)).Should().BeTrue();
    }
}
