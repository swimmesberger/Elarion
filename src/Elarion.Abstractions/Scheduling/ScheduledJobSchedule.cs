using System.Globalization;
using Elarion.Abstractions.Substitution;
using Microsoft.Extensions.Configuration;

namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Distinguishes how a scheduled job computes its first and next due times.
/// </summary>
public enum ScheduledJobScheduleKind {
    /// <summary>
    /// Occurrences are aligned to a fixed-rate grid anchored at the previous due time.
    /// </summary>
    /// <remarks>
    /// Execution duration does not shift the grid. Late or missed grid slots are handled by
    /// <see cref="ScheduledJobMisfirePolicy"/>.
    /// </remarks>
    FixedRate,

    /// <summary>
    /// The next occurrence is due a fixed delay after the previous run completes.
    /// </summary>
    /// <remarks>
    /// This behaves like a polling loop: work finishes first, then the delay starts.
    /// </remarks>
    FixedDelay,

    /// <summary>
    /// Occurrences follow a cron expression evaluated in the configured time zone.
    /// </summary>
    Cron,

    /// <summary>
    /// The job runs once after startup, optionally delayed.
    /// </summary>
    OneTime
}

/// <summary>
/// Defines a recurring schedule for a compile-time known job. Values may be literals or
/// <c>${Config:Key}</c> / <c>${Config:Key:-default}</c> placeholders that are re-resolved
/// against configuration for every occurrence, so intervals can change at runtime.
/// Fixed-rate and cron missed-occurrence behavior is controlled by
/// <see cref="ScheduledJobDescriptor.MisfirePolicy"/>.
/// </summary>
public sealed record ScheduledJobSchedule {
    /// <summary>
    /// Spring-compatible cron value that disables a cron trigger.
    /// </summary>
    /// <remarks>
    /// This is useful with placeholders, for example <c>${Jobs:DailyCron:--}</c>, so a missing
    /// configuration value disables the schedule instead of throwing.
    /// </remarks>
    public const string CronDisabled = "-";

    /// <summary>How the next due time is computed.</summary>
    public required ScheduledJobScheduleKind Kind { get; init; }

    /// <summary>
    /// Duration text for fixed-rate/fixed-delay schedules or cron expression text for cron schedules.
    /// </summary>
    /// <remarks>
    /// May be a literal value or a <c>${Config:Key}</c> / <c>${Config:Key:-fallback}</c>
    /// placeholder resolved by <see cref="Resolve(IConfiguration)"/>.
    /// </remarks>
    public required string Value { get; init; }

    /// <summary>Optional delay before the first execution, possibly a placeholder.</summary>
    public string? InitialDelay { get; init; }

    /// <summary>
    /// Whether interval schedules are due immediately at host startup.
    /// </summary>
    /// <remarks>
    /// Applies to fixed-rate and fixed-delay schedules without <see cref="InitialDelay"/>.
    /// Cron schedules ignore this value.
    /// </remarks>
    public bool RunOnStart { get; init; } = true;

    /// <summary>Optional time zone id for cron evaluation (defaults to UTC), possibly a placeholder.</summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Creates a grid-aligned fixed-rate schedule, e.g. <c>"15m"</c> or <c>"${Jobs:Interval:-15m}"</c>.
    /// </summary>
    /// <remarks>
    /// Fixed-rate should be used when due times matter independently of execution duration,
    /// such as "every minute on the minute".
    /// </remarks>
    public static ScheduledJobSchedule FixedRate(string every, string? initialDelay = null, bool runOnStart = true) =>
        CreateInterval(ScheduledJobScheduleKind.FixedRate, every, initialDelay, runOnStart);

    /// <summary>
    /// Creates a schedule due a fixed delay after each run completes.
    /// </summary>
    /// <remarks>
    /// Fixed-delay should be used when the job should wait after each completed pass, such as
    /// polling or cleanup loops.
    /// </remarks>
    public static ScheduledJobSchedule FixedDelay(string delay, string? initialDelay = null, bool runOnStart = true) =>
        CreateInterval(ScheduledJobScheduleKind.FixedDelay, delay, initialDelay, runOnStart);

    /// <summary>
    /// Creates a cron schedule, e.g. <c>"0 0 3 * * *"</c>, evaluated in <paramref name="timeZone"/> (UTC by default).
    /// </summary>
    /// <remarks>
    /// The expression may have five fields (minute precision, seconds default to zero) or six
    /// fields (second precision). Use <see cref="CronDisabled"/> to disable the schedule.
    /// </remarks>
    public static ScheduledJobSchedule Cron(string expression, string? timeZone = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        // Note 19: Literal cron expressions are validated eagerly; placeholders are validated later after configuration is resolved.
        if (!VariableSubstitution.IsPlaceholder(expression) && expression != CronDisabled) {
            CronExpression.Parse(expression);
        }

        // Note 20: Time zone ids are also validated eagerly unless they are placeholders.
        if (timeZone is not null && !VariableSubstitution.IsPlaceholder(timeZone)) {
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }

        return new ScheduledJobSchedule {
            Kind = ScheduledJobScheduleKind.Cron,
            Value = expression,
            TimeZone = timeZone
        };
    }

    /// <summary>
    /// Creates a one-time startup schedule due after <paramref name="initialDelay"/>.
    /// </summary>
    /// <remarks>
    /// This is for compile-time startup work, not for user/request-created delayed jobs. Use
    /// <see cref="IJobScheduler"/> for runtime one-off jobs.
    /// </remarks>
    public static ScheduledJobSchedule Once(string initialDelay) {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialDelay);
        if (!VariableSubstitution.IsPlaceholder(initialDelay)) {
            ParseDuration(initialDelay);
        }

        return new ScheduledJobSchedule {
            Kind = ScheduledJobScheduleKind.OneTime,
            Value = initialDelay,
            InitialDelay = initialDelay,
            RunOnStart = false
        };
    }

    /// <summary>
    /// Resolves placeholders against configuration and parses the schedule values.
    /// </summary>
    /// <exception cref="InvalidOperationException">A placeholder is unresolvable.</exception>
    /// <exception cref="FormatException">A resolved value is not valid.</exception>
    public ResolvedSchedule Resolve(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        return Resolve(new ConfigurationVariableSource(configuration));
    }

    /// <summary>
    /// Resolves placeholders against an arbitrary <see cref="IVariableSource"/> and parses the schedule
    /// values, so a schedule can draw its variables from configuration, settings, or any other source.
    /// </summary>
    /// <exception cref="InvalidOperationException">A placeholder is unresolvable.</exception>
    /// <exception cref="FormatException">A resolved value is not valid.</exception>
    public ResolvedSchedule Resolve(IVariableSource variables) {
        ArgumentNullException.ThrowIfNull(variables);
        // Note 21: Resolution is separated from construction so configuration changes can affect future occurrences.
        var value = VariableSubstitution.ResolveRequired(Value, variables);
        var initialDelay = InitialDelay is null
            ? (TimeSpan?)null
            : ParseDuration(VariableSubstitution.ResolveRequired(InitialDelay, variables));

        if (Kind == ScheduledJobScheduleKind.Cron) {
            if (value == CronDisabled) {
                return new ResolvedSchedule {
                    Kind = Kind,
                    TimeZone = TimeZoneInfo.Utc,
                    IsDisabled = true
                };
            }

            var timeZoneId = TimeZone is null ? null : VariableSubstitution.ResolveRequired(TimeZone, variables);
            return new ResolvedSchedule {
                Kind = Kind,
                Cron = CronExpression.Parse(value),
                TimeZone = timeZoneId is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId),
                InitialDelay = initialDelay,
                RunOnStart = RunOnStart
            };
        }

        if (Kind == ScheduledJobScheduleKind.OneTime) {
            return new ResolvedSchedule {
                Kind = Kind,
                Interval = null,
                TimeZone = TimeZoneInfo.Utc,
                InitialDelay = ParseDuration(value),
                RunOnStart = false
            };
        }

        return new ResolvedSchedule {
            Kind = Kind,
            Interval = ParsePositiveDuration(value),
            TimeZone = TimeZoneInfo.Utc,
            InitialDelay = initialDelay,
            RunOnStart = RunOnStart
        };
    }

    internal static DateTimeOffset GetNextGridOccurrence(
        DateTimeOffset previousDueTimeUtc,
        DateTimeOffset nowUtc,
        TimeSpan every) {
        var next = previousDueTimeUtc + every;
        if (next > nowUtc) {
            return next;
        }

        // Note 22: Fixed-rate schedules stay aligned to their original grid instead of drifting by "now + interval".
        var missedIntervals = (nowUtc - previousDueTimeUtc).Ticks / every.Ticks;
        return previousDueTimeUtc + TimeSpan.FromTicks(every.Ticks * (missedIntervals + 1));
    }

    internal static TimeSpan ParseDuration(string value) {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)) {
            // Note 23: Invariant TimeSpan keeps parsing stable regardless of the server's culture.
            return parsed;
        }

        string numberText;
        Func<double, TimeSpan>? factory;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
            numberText = value[..^2];
            factory = TimeSpan.FromMilliseconds;
        } else {
            numberText = value.Length > 0 ? value[..^1] : value;
            factory = value.Length > 0
                ? value[^1] switch {
                    'd' or 'D' => TimeSpan.FromDays,
                    'h' or 'H' => TimeSpan.FromHours,
                    'm' or 'M' => TimeSpan.FromMinutes,
                    's' or 'S' => TimeSpan.FromSeconds,
                    _ => null
                }
                : null;
        }

        if (factory is null ||
            numberText.Length == 0 ||
            !double.TryParse(numberText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount)) {
            // Note 24: Throwing FormatException here gives configuration users a precise startup/runtime error.
            throw new FormatException(
                $"Duration '{value}' is invalid. Use invariant TimeSpan text or a number with the suffix ms, s, m, h, or d.");
        }

        return factory(amount);
    }

    private static ScheduledJobSchedule CreateInterval(
        ScheduledJobScheduleKind kind,
        string value,
        string? initialDelay,
        bool runOnStart) {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!VariableSubstitution.IsPlaceholder(value)) {
            ParsePositiveDuration(value);
        }

        if (initialDelay is not null && !VariableSubstitution.IsPlaceholder(initialDelay)) {
            ParseDuration(initialDelay);
        }

        return new ScheduledJobSchedule {
            Kind = kind,
            Value = value,
            InitialDelay = string.IsNullOrWhiteSpace(initialDelay) ? null : initialDelay,
            RunOnStart = runOnStart
        };
    }

    private static TimeSpan ParsePositiveDuration(string value) {
        var interval = ParseDuration(value);
        if (interval <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(value), $"The scheduled interval '{value}' must be greater than zero.");
        }

        return interval;
    }
}

/// <summary>
/// A schedule with all placeholders resolved and values parsed; computes due times.
/// </summary>
/// <remarks>
/// This type is produced from <see cref="ScheduledJobSchedule.Resolve(IConfiguration)"/> and
/// is primarily used by the scheduler runtime. It separates parsing/configuration errors from
/// due-time calculation.
/// </remarks>
public sealed record ResolvedSchedule {
    /// <summary>How the next due time is computed.</summary>
    public required ScheduledJobScheduleKind Kind { get; init; }

    /// <summary>
    /// Parsed interval for fixed-rate and fixed-delay schedules; null for cron and one-time schedules.
    /// </summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>The parsed expression for cron schedules.</summary>
    public CronExpression? Cron { get; init; }

    /// <summary>The time zone cron expressions are evaluated in.</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Optional delay before the first execution.</summary>
    public TimeSpan? InitialDelay { get; init; }

    /// <summary>Whether the first execution is due immediately at host startup.</summary>
    public bool RunOnStart { get; init; } = true;

    /// <summary>
    /// True when a cron expression resolved to the disabled sentinel <see cref="ScheduledJobSchedule.CronDisabled"/>.
    /// </summary>
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Computes the first due time after host startup.
    /// </summary>
    /// <remarks>
    /// Throws when <see cref="IsDisabled"/> is true because disabled schedules intentionally
    /// have no due time.
    /// </remarks>
    public DateTimeOffset GetFirstDueTime(DateTimeOffset nowUtc) {
        if (IsDisabled) {
            throw new InvalidOperationException("A disabled schedule does not have a due time.");
        }

        if (InitialDelay is { } initialDelay) {
            nowUtc += initialDelay;
            return Kind == ScheduledJobScheduleKind.Cron ? Cron!.GetNextOccurrence(nowUtc, TimeZone) : nowUtc;
        }

        return Kind switch {
            ScheduledJobScheduleKind.Cron => Cron!.GetNextOccurrence(nowUtc, TimeZone),
            ScheduledJobScheduleKind.OneTime => nowUtc,
            _ => RunOnStart ? nowUtc : nowUtc + Interval!.Value
        };
    }

    /// <summary>
    /// Computes the next due time strictly after <paramref name="nowUtc"/>. For fixed-rate
    /// schedules this stays on the grid anchored at <paramref name="previousDueTimeUtc"/>
    /// and skips occurrences already in the past; for fixed-delay schedules (where it is
    /// called after the run completes) it is simply now plus the delay.
    /// </summary>
    /// <remarks>
    /// Misfire policy is handled by the scheduler before it calls this method. This method
    /// returns one next due time, not a list of all missed occurrences.
    /// </remarks>
    public DateTimeOffset GetNextDueTime(DateTimeOffset previousDueTimeUtc, DateTimeOffset nowUtc) =>
        Kind switch {
            ScheduledJobScheduleKind.FixedRate =>
                ScheduledJobSchedule.GetNextGridOccurrence(previousDueTimeUtc, nowUtc, Interval!.Value),
            ScheduledJobScheduleKind.FixedDelay => nowUtc + Interval!.Value,
            ScheduledJobScheduleKind.Cron => Cron!.GetNextOccurrence(nowUtc, TimeZone),
            _ => throw new InvalidOperationException("A one-time schedule does not have a next due time.")
        };
}
