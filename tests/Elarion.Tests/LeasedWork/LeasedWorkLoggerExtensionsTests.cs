using AwesomeAssertions;
using Elarion.EntityFrameworkCore.LeasedWork;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.LeasedWork;

public sealed class LeasedWorkLoggerExtensionsTests {
    private static readonly Guid Id = Guid.CreateVersion7();

    [Fact]
    public void ClaimLost_IsAWarning_NamingTheQueueAndTheRow() {
        var logger = new CapturingLogger();

        logger.LogFinalizeOutcome(LeasedWorkFinalizeOutcome.ClaimLost, "delivery", Id).Should().BeFalse();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("delivery").And.Contain(Id.ToString()).And.Contain("lost");
    }

    [Fact]
    public void Ineligible_IsInformation() {
        var logger = new CapturingLogger();

        logger.LogFinalizeOutcome(LeasedWorkFinalizeOutcome.Ineligible, "metadata", Id).Should().BeFalse();

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void Finalized_LogsNothing() {
        var logger = new CapturingLogger();

        logger.LogFinalizeOutcome(LeasedWorkFinalizeOutcome.Finalized, "delivery", Id).Should().BeTrue();
        logger.LogFinalizeOutcome(true, "delivery", Id).Should().BeTrue();

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AFalseBooleanFinalize_IsALostClaim() {
        var logger = new CapturingLogger();

        logger.LogFinalizeOutcome(false, "delivery", Id).Should().BeFalse();

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    private sealed class CapturingLogger : ILogger {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
