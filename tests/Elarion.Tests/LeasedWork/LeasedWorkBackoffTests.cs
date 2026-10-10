using AwesomeAssertions;
using Elarion.EntityFrameworkCore.LeasedWork;
using Xunit;

namespace Elarion.Tests.LeasedWork;

public sealed class LeasedWorkBackoffTests {
    private static readonly TimeSpan[] Steps = [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)
    ];

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(4, 40)]
    public void Exponential_DoublesFromTheBaseDelay(int attempt, int expectedSeconds) {
        LeasedWorkBackoff.Exponential(attempt, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Exponential_IsCappedAtTheMaximum() {
        LeasedWorkBackoff.Exponential(12, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1))
            .Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Exponential_NeverOverflowsForHugeAttemptCounts() {
        LeasedWorkBackoff.Exponential(int.MaxValue, TimeSpan.FromDays(1), TimeSpan.FromDays(3))
            .Should().Be(TimeSpan.FromDays(3));
    }

    [Fact]
    public void Exponential_WithZeroBase_RetriesImmediately() {
        LeasedWorkBackoff.Exponential(3, TimeSpan.Zero, TimeSpan.FromHours(1)).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 60)]
    [InlineData(9, 60)]
    public void Ladder_TakesTheAttemptsStep_AndRepeatsTheLast(int attempt, int expectedMinutes) {
        LeasedWorkBackoff.Ladder(attempt, Steps).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void Ladder_HonorsALongerRetryAfterHint() {
        LeasedWorkBackoff.Ladder(1, Steps, TimeSpan.FromMinutes(30)).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Ladder_IgnoresAShorterRetryAfterHint() {
        LeasedWorkBackoff.Ladder(2, Steps, TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Ladder_RejectsAnEmptyLadder() {
        var act = () => LeasedWorkBackoff.Ladder(1, []);
        act.Should().Throw<ArgumentException>();
    }
}
