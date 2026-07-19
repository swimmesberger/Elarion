using AwesomeAssertions;
using Elarion.Abstractions.Scheduling;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class CronExpressionTests {
    // 2026-06-12 is a Friday.
    private static readonly DateTimeOffset Anchor = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0 0 3 * * *", "2026-06-13T03:00:00Z")] // daily at 03:00, already past today
    [InlineData("0 15 10 15 * *", "2026-06-15T10:15:00Z")] // monthly on the 15th at 10:15
    [InlineData("*/15 * * * * *", "2026-06-12T12:00:15Z")] // every 15 seconds
    [InlineData("0 */5 * * * *", "2026-06-12T12:05:00Z")] // every 5 minutes
    [InlineData("0 0 8-10,18 * * *", "2026-06-12T18:00:00Z")] // ranges and lists
    [InlineData("0 0 12 * JAN MON", "2027-01-04T12:00:00Z")] // month and day names
    [InlineData("0 0 0 ? * MON", "2026-06-15T00:00:00Z")] // ? wildcard, next Monday
    [InlineData("0 0 0 * * 7", "2026-06-14T00:00:00Z")] // 7 means Sunday
    [InlineData("0 30 6/2 * * *", "2026-06-12T12:30:00Z")] // open-ended step from 6 every 2 hours
    public void GetNextOccurrence_SixFieldExpressions_ComputesNextUtcInstant(string expression, string expected) {
        var cron = CronExpression.Parse(expression);

        var next = cron.GetNextOccurrence(Anchor);

        next.Should().Be(DateTimeOffset.Parse(expected));
    }

    [Fact]
    public void Parse_FiveFieldExpression_DefaultsSecondsToZero() {
        // Mondays at 14:30 in classic Unix five-field form.
        var cron = CronExpression.Parse("30 14 * * 1");

        var next = cron.GetNextOccurrence(Anchor);

        next.Should().Be(DateTimeOffset.Parse("2026-06-15T14:30:00Z"));
    }

    [Fact]
    public void GetNextOccurrence_DayOfMonthAndDayOfWeekBothRestricted_MatchesEither() {
        // Unix cron semantics: fires on the 1st of the month OR on Mondays.
        var cron = CronExpression.Parse("0 0 0 1 * MON");

        var next = cron.GetNextOccurrence(Anchor);

        next.Should().Be(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
    }

    [Fact]
    public void GetNextOccurrence_ChainedCalls_ProduceStrictlyIncreasingInstants() {
        var cron = CronExpression.Parse("0 0 3 * * *");

        var first = cron.GetNextOccurrence(Anchor);
        var second = cron.GetNextOccurrence(first);

        second.Should().Be(first.AddDays(1));
    }

    [Fact]
    public void GetNextOccurrence_WithTimeZone_EvaluatesWallClockTime() {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        var cron = CronExpression.Parse("0 0 3 * * *");

        // 03:00 wall clock at +02:00 is 01:00 UTC.
        var next = cron.GetNextOccurrence(DateTimeOffset.Parse("2026-06-12T00:00:00Z"), zone);

        next.Should().Be(DateTimeOffset.Parse("2026-06-12T01:00:00Z"));
    }

    [Fact]
    public void GetNextOccurrence_UnreachableExpression_Throws() {
        // February 30th never exists.
        var cron = CronExpression.Parse("0 0 0 30 2 *");

        var act = () => cron.GetNextOccurrence(Anchor);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * *")]
    [InlineData("* * * * * * *")]
    [InlineData("0 0 25 * * *")]
    [InlineData("0 61 * * * *")]
    [InlineData("0 0 0 * 13 *")]
    [InlineData("0 0 0 * * 8")]
    [InlineData("0 0 0 * FOO *")]
    [InlineData("0 0/0 * * * *")]
    [InlineData("0 30-10 * * * *")]
    public void Parse_InvalidExpression_Throws(string expression) {
        var act = () => CronExpression.Parse(expression);

        act.Should().Throw<Exception>().Which.Should().Match(e => e is FormatException || e is ArgumentException);
    }
}
