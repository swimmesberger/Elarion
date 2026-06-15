namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// A parsed cron expression with second-level precision.
/// </summary>
/// <remarks>
/// Supports six fields (<c>second minute hour day-of-month month day-of-week</c>) or the
/// classic five-field Unix form (seconds default to <c>0</c>). Fields accept <c>*</c>,
/// <c>?</c>, lists (<c>1,15</c>), ranges (<c>8-18</c>), steps (<c>*/5</c>, <c>10-30/5</c>),
/// month names (<c>JAN</c>-<c>DEC</c>), and day names (<c>SUN</c>-<c>SAT</c>, with both
/// <c>0</c> and <c>7</c> meaning Sunday). When day-of-month and day-of-week are both
/// restricted, a date matches if either field matches (Unix cron semantics).
/// Quartz extensions (<c>L</c>, <c>W</c>, <c>#</c>) are not supported.
/// </remarks>
public sealed class CronExpression {
    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayOfWeekNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly ulong _seconds;
    private readonly ulong _minutes;
    private readonly uint _hours;
    private readonly uint _daysOfMonth;
    private readonly ushort _months;
    private readonly byte _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;
    private readonly string _text;

    private CronExpression(
        ulong seconds,
        ulong minutes,
        uint hours,
        uint daysOfMonth,
        ushort months,
        byte daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted,
        string text) {
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
        _text = text;
    }

    /// <summary>Parses a five- or six-field cron expression.</summary>
    /// <exception cref="FormatException">The expression is not valid cron syntax.</exception>
    public static CronExpression Parse(string expression) {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var fields = expression.Split(' ', '\t');
        fields = Array.FindAll(fields, static field => field.Length > 0);
        if (fields.Length == 5) {
            fields = ["0", .. fields];
        }

        if (fields.Length != 6) {
            throw new FormatException(
                $"Cron expression '{expression}' must have 5 or 6 space-separated fields (second minute hour day-of-month month day-of-week).");
        }

        var seconds = ParseField(fields[0], 0, 59, null, expression);
        var minutes = ParseField(fields[1], 0, 59, null, expression);
        var hours = ParseField(fields[2], 0, 23, null, expression);
        var daysOfMonth = ParseField(fields[3], 1, 31, null, expression);
        var months = ParseField(fields[4], 1, 12, MonthNames, expression);
        var daysOfWeek = ParseField(fields[5], 0, 7, DayOfWeekNames, expression);

        // Both 0 and 7 mean Sunday in day-of-week.
        if ((daysOfWeek & (1UL << 7)) != 0) {
            daysOfWeek = (daysOfWeek & ~(1UL << 7)) | 1UL;
        }

        return new CronExpression(
            seconds,
            minutes,
            (uint)hours,
            (uint)daysOfMonth,
            (ushort)(months >> 1),
            (byte)daysOfWeek,
            dayOfMonthRestricted: !IsFullRange(fields[3]),
            dayOfWeekRestricted: !IsFullRange(fields[5]),
            expression);
    }

    /// <summary>Computes the next UTC occurrence strictly after <paramref name="afterUtc"/>.</summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc) =>
        GetNextOccurrence(afterUtc, TimeZoneInfo.Utc);

    /// <summary>
    /// Computes the next occurrence strictly after <paramref name="afterUtc"/>, evaluating
    /// the expression as wall-clock time in <paramref name="timeZone"/>.
    /// </summary>
    /// <remarks>
    /// Local times skipped by a daylight-saving transition do not fire; ambiguous local
    /// times resolve to the standard-time instant.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The expression never matches.</exception>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo timeZone) {
        var local = TimeZoneInfo.ConvertTime(afterUtc, timeZone).DateTime;
        var candidate = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond)).AddSeconds(1);
        var limit = candidate.AddYears(5);

        while (candidate < limit) {
            if ((_months & (1 << (candidate.Month - 1))) == 0) {
                candidate = new DateTime(candidate.Year, candidate.Month, 1).AddMonths(1);
                continue;
            }

            if (!DayMatches(candidate)) {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if ((_hours & (1u << candidate.Hour)) == 0) {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if ((_minutes & (1UL << candidate.Minute)) == 0) {
                candidate = candidate.Date.AddHours(candidate.Hour).AddMinutes(candidate.Minute + 1);
                continue;
            }

            if ((_seconds & (1UL << candidate.Second)) == 0) {
                candidate = candidate.AddSeconds(1);
                continue;
            }

            if (timeZone.IsInvalidTime(candidate)) {
                candidate = candidate.AddSeconds(1);
                continue;
            }

            var result = new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));
            if (result > afterUtc) {
                return result;
            }

            candidate = candidate.AddSeconds(1);
        }

        throw new InvalidOperationException(
            $"Cron expression '{_text}' has no occurrence within the next five years.");
    }

    /// <inheritdoc />
    public override string ToString() => _text;

    private bool DayMatches(DateTime date) {
        var dayOfMonthMatches = (_daysOfMonth & (1u << date.Day)) != 0;
        var dayOfWeekMatches = (_daysOfWeek & (1 << (int)date.DayOfWeek)) != 0;

        if (_dayOfMonthRestricted && _dayOfWeekRestricted) {
            return dayOfMonthMatches || dayOfWeekMatches;
        }

        return (!_dayOfMonthRestricted || dayOfMonthMatches) &&
               (!_dayOfWeekRestricted || dayOfWeekMatches);
    }

    private static bool IsFullRange(string field) => field is "*" or "?";

    private static ulong ParseField(string field, int min, int max, string[]? names, string expression) {
        var bits = 0UL;
        foreach (var part in field.Split(',')) {
            bits |= ParsePart(part, min, max, names, expression);
        }

        return bits;
    }

    private static ulong ParsePart(string part, int min, int max, string[]? names, string expression) {
        var step = 1;
        var rangeText = part;
        var slashIndex = part.IndexOf('/');
        if (slashIndex >= 0) {
            rangeText = part[..slashIndex];
            var stepText = part[(slashIndex + 1)..];
            if (!int.TryParse(stepText, out step) || step <= 0) {
                throw new FormatException($"Cron expression '{expression}' has an invalid step '{part}'.");
            }
        }

        int from;
        int to;
        if (rangeText is "*" or "?") {
            from = min;
            to = max;
        } else {
            var dashIndex = rangeText.IndexOf('-');
            if (dashIndex >= 0) {
                from = ParseValue(rangeText[..dashIndex], min, max, names, expression);
                to = ParseValue(rangeText[(dashIndex + 1)..], min, max, names, expression);
                if (to < from) {
                    throw new FormatException($"Cron expression '{expression}' has an inverted range '{part}'.");
                }
            } else {
                from = ParseValue(rangeText, min, max, names, expression);
                // "n/step" means from n to the field maximum in steps.
                to = slashIndex >= 0 ? max : from;
            }
        }

        var bits = 0UL;
        for (var value = from; value <= to; value += step) {
            bits |= 1UL << value;
        }

        return bits;
    }

    private static int ParseValue(string text, int min, int max, string[]? names, string expression) {
        if (names is not null) {
            for (var i = 0; i < names.Length; i++) {
                if (string.Equals(text, names[i], StringComparison.OrdinalIgnoreCase)) {
                    // Named values map onto the numeric range starting at its minimum
                    // (months are 1-based, days of week 0-based).
                    return min == 0 ? i : i + min;
                }
            }
        }

        if (!int.TryParse(text, out var value) || value < min || value > max) {
            throw new FormatException(
                $"Cron expression '{expression}' has the value '{text}' outside the range {min}-{max}.");
        }

        return value;
    }
}
