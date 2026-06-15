using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Elarion.Abstractions.Scheduling;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class ScheduledJobScheduleTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();
    private static readonly DateTimeOffset Anchor = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("50ms", 50)]
    [InlineData("250MS", 250)]
    [InlineData("1.5s", 1_500)]
    [InlineData("30s", 30_000)]
    [InlineData("15m", 900_000)]
    [InlineData("6h", 21_600_000)]
    [InlineData("1d", 86_400_000)]
    [InlineData("00:00:00.050", 50)]
    [InlineData("01:30:00", 5_400_000)]
    public void FixedRate_ParsesDurations(string text, double expectedMilliseconds)
    {
        var resolved = ScheduledJobSchedule.FixedRate(text).Resolve(EmptyConfiguration);

        resolved.Interval!.Value.TotalMilliseconds.Should().Be(expectedMilliseconds);
    }

    [Theory]
    [InlineData("ms")]
    [InlineData("50xs")]
    [InlineData("abc")]
    [InlineData("m5")]
    public void FixedRate_InvalidDuration_ThrowsEagerly(string text)
    {
        var act = () => ScheduledJobSchedule.FixedRate(text);

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("-5m")]
    public void FixedRate_NonPositiveDuration_ThrowsEagerly(string text)
    {
        var act = () => ScheduledJobSchedule.FixedRate(text);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Cron_InvalidExpression_ThrowsEagerly()
    {
        var act = () => ScheduledJobSchedule.Cron("not cron");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Cron_DisabledSentinel_ResolvesAsDisabled()
    {
        var resolved = ScheduledJobSchedule.Cron("-").Resolve(EmptyConfiguration);

        resolved.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void Cron_DisabledPlaceholder_ResolvesAsDisabled()
    {
        var resolved = ScheduledJobSchedule.Cron("${Jobs:Cron:--}").Resolve(EmptyConfiguration);

        resolved.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void GetFirstDueTime_DisabledCron_Throws()
    {
        var resolved = ScheduledJobSchedule.Cron("-").Resolve(EmptyConfiguration);

        var act = () => resolved.GetFirstDueTime(Anchor);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Once_InitialDelay_IsDueAfterDelay()
    {
        var resolved = ScheduledJobSchedule.Once("250ms").Resolve(EmptyConfiguration);

        resolved.Kind.Should().Be(ScheduledJobScheduleKind.OneTime);
        resolved.GetFirstDueTime(Anchor).Should().Be(Anchor + TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Placeholder_DefersValidationToResolve()
    {
        var schedule = ScheduledJobSchedule.FixedRate("${Jobs:Interval}");

        var act = () => schedule.Resolve(EmptyConfiguration);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Placeholder_WithInlineDefault_UsesDefaultWhenNotConfigured()
    {
        var resolved = ScheduledJobSchedule.FixedRate("${Jobs:Interval:-25ms}").Resolve(EmptyConfiguration);

        resolved.Interval.Should().Be(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void Placeholder_ConfiguredValue_OverridesInlineDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Interval"] = "75ms" })
            .Build();

        var resolved = ScheduledJobSchedule.FixedRate("${Jobs:Interval:-25ms}").Resolve(configuration);

        resolved.Interval.Should().Be(TimeSpan.FromMilliseconds(75));
    }

    [Fact]
    public void Placeholder_InvalidConfiguredValue_ThrowsOnResolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Interval"] = "often" })
            .Build();

        var act = () => ScheduledJobSchedule.FixedRate("${Jobs:Interval:-25ms}").Resolve(configuration);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void GetFirstDueTime_RunOnStart_IsDueImmediately()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms").Resolve(EmptyConfiguration);

        resolved.GetFirstDueTime(Anchor).Should().Be(Anchor);
    }

    [Fact]
    public void GetFirstDueTime_WithoutRunOnStart_IsDueAfterOneInterval()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms", runOnStart: false).Resolve(EmptyConfiguration);

        resolved.GetFirstDueTime(Anchor).Should().Be(Anchor + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void GetFirstDueTime_WithInitialDelay_UsesTheDelay()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms", initialDelay: "200ms").Resolve(EmptyConfiguration);

        resolved.GetFirstDueTime(Anchor).Should().Be(Anchor + TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void GetFirstDueTime_CronSchedule_IsNextCronOccurrence()
    {
        var resolved = ScheduledJobSchedule.Cron("0 0 3 * * *").Resolve(EmptyConfiguration);

        resolved.GetFirstDueTime(Anchor).Should().Be(DateTimeOffset.Parse("2026-06-13T03:00:00Z"));
    }

    [Fact]
    public void GetNextDueTime_FixedRate_AdvancesOneInterval()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms").Resolve(EmptyConfiguration);

        var next = resolved.GetNextDueTime(Anchor, Anchor + TimeSpan.FromMilliseconds(10));

        next.Should().Be(Anchor + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void GetNextDueTime_FixedRateMissedSlots_SkipsToFirstFutureSlotOnTheGrid()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms").Resolve(EmptyConfiguration);

        // 10 seconds elapsed: 200 slots were missed; the next due time stays grid-aligned.
        var next = resolved.GetNextDueTime(Anchor, Anchor + TimeSpan.FromSeconds(10));

        next.Should().Be(Anchor + TimeSpan.FromMilliseconds(201 * 50));
    }

    [Fact]
    public void GetNextDueTime_FixedRateClockWentBackwards_KeepsTheOriginalNextSlot()
    {
        var resolved = ScheduledJobSchedule.FixedRate("50ms").Resolve(EmptyConfiguration);

        var next = resolved.GetNextDueTime(Anchor, Anchor - TimeSpan.FromMinutes(5));

        next.Should().Be(Anchor + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void GetNextDueTime_FixedDelay_MeasuresFromNow()
    {
        var resolved = ScheduledJobSchedule.FixedDelay("50ms").Resolve(EmptyConfiguration);

        // For fixed delay the scheduler passes completion time as "now"; the previous
        // due time does not matter.
        var completionTime = Anchor + TimeSpan.FromSeconds(3);
        var next = resolved.GetNextDueTime(Anchor, completionTime);

        next.Should().Be(completionTime + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void GetNextDueTime_Cron_ReturnsNextOccurrenceAfterNow()
    {
        var resolved = ScheduledJobSchedule.Cron("0 0 3 * * *").Resolve(EmptyConfiguration);

        var next = resolved.GetNextDueTime(Anchor, Anchor + TimeSpan.FromDays(10));

        next.Should().Be(DateTimeOffset.Parse("2026-06-23T03:00:00Z"));
    }

    [Fact]
    public void GetNextDueTime_OneTime_Throws()
    {
        var resolved = ScheduledJobSchedule.Once("1s").Resolve(EmptyConfiguration);

        var act = () => resolved.GetNextDueTime(Anchor, Anchor);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_CronWithTimeZonePlaceholder_ResolvesZoneFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Zone"] = "UTC" })
            .Build();

        var resolved = ScheduledJobSchedule.Cron("0 0 3 * * *", timeZone: "${Jobs:Zone}").Resolve(configuration);

        resolved.TimeZone.Should().Be(TimeZoneInfo.Utc);
    }
}
