using System.Globalization;
using System.Text;
using Elarion.Calendars.Reading;

namespace Elarion.Calendars;

/// <summary>The <c>FREQ</c> of a recurrence rule.</summary>
public enum CalendarFrequency {
    /// <summary><c>SECONDLY</c>.</summary>
    Secondly,

    /// <summary><c>MINUTELY</c>.</summary>
    Minutely,

    /// <summary><c>HOURLY</c>.</summary>
    Hourly,

    /// <summary><c>DAILY</c>.</summary>
    Daily,

    /// <summary><c>WEEKLY</c>.</summary>
    Weekly,

    /// <summary><c>MONTHLY</c>.</summary>
    Monthly,

    /// <summary><c>YEARLY</c>.</summary>
    Yearly,
}

/// <summary>One <c>BYDAY</c> value: a weekday, and for a monthly or yearly rule optionally its position (<c>2TU</c>, <c>-1SU</c>).</summary>
/// <param name="DayOfWeek">The weekday.</param>
/// <param name="Offset">The position within the month or year, negative from its end, or <c>0</c> for every such weekday.</param>
public readonly record struct CalendarWeekday(DayOfWeek DayOfWeek, int Offset = 0) {
    /// <summary>The value in iCalendar notation, such as <c>-1SU</c>.</summary>
    public override string ToString() =>
        (Offset == 0 ? "" : Offset.ToString(CultureInfo.InvariantCulture)) + CalendarRecurrenceRule.DayCode(DayOfWeek);
}

/// <summary>
/// An RFC 5545 recurrence rule (<c>RRULE</c>) for the Gregorian calendar (spec 0004 FR-031): every <c>FREQ</c>, every
/// <c>BY</c> part, <c>BYSETPOS</c>, <c>WKST</c>, <c>COUNT</c>, <c>UNTIL</c> and <c>INTERVAL</c>.
/// </summary>
/// <example>
/// <code>
/// CalendarRecurrenceRule.TryParse("FREQ=MONTHLY;BYDAY=TU;BYSETPOS=2", out var rule);
/// var weekly = new CalendarRecurrenceRule { Frequency = CalendarFrequency.Weekly, ByDay = [new(DayOfWeek.Tuesday)], Count = 10 };
/// </code>
/// </example>
public sealed record CalendarRecurrenceRule {
    /// <summary>The frequency.</summary>
    public required CalendarFrequency Frequency { get; init; }

    /// <summary>The interval between periods, 1 or more.</summary>
    public int Interval { get; init; } = 1;

    /// <summary>The number of occurrences, the first counted; <see langword="null"/> without a count.</summary>
    public int? Count { get; init; }

    /// <summary>The last possible occurrence, inclusive; <see langword="null"/> without an end.</summary>
    public CalendarDateTime? Until { get; init; }

    /// <summary><c>BYSECOND</c>.</summary>
    public IReadOnlyList<int> BySecond { get; init; } = [];

    /// <summary><c>BYMINUTE</c>.</summary>
    public IReadOnlyList<int> ByMinute { get; init; } = [];

    /// <summary><c>BYHOUR</c>.</summary>
    public IReadOnlyList<int> ByHour { get; init; } = [];

    /// <summary><c>BYDAY</c>.</summary>
    public IReadOnlyList<CalendarWeekday> ByDay { get; init; } = [];

    /// <summary><c>BYMONTHDAY</c>, negative from the month's end.</summary>
    public IReadOnlyList<int> ByMonthDay { get; init; } = [];

    /// <summary><c>BYYEARDAY</c>, negative from the year's end.</summary>
    public IReadOnlyList<int> ByYearDay { get; init; } = [];

    /// <summary><c>BYWEEKNO</c>, negative from the year's end.</summary>
    public IReadOnlyList<int> ByWeekNo { get; init; } = [];

    /// <summary><c>BYMONTH</c>.</summary>
    public IReadOnlyList<int> ByMonth { get; init; } = [];

    /// <summary><c>BYSETPOS</c>, negative from the period's end.</summary>
    public IReadOnlyList<int> BySetPosition { get; init; } = [];

    /// <summary>The first day of the week, <c>WKST</c>; Monday by default.</summary>
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

    /// <summary>
    /// Parses a rule. An <c>RSCALE</c> other than <c>GREGORIAN</c> (FR-037), an unknown <c>FREQ</c>, a part out of
    /// range does not parse. Repeated values of a <c>BY</c> list are allowed, as RFC 5545 allows them, and kept once, so
    /// a list holds at most as many values as its part has however long the text is (<c>BYMONTH=1,1,1</c> is January).
    /// Parsing is linear in the text's length, with no allocation per list item; a reader bounds that length by
    /// <see cref="CalendarReadOptions.MaxLineBytes"/>, reported as <see cref="CalendarErrorCodes.LineTooLong"/>. Unknown
    /// parts such as <c>SKIP</c> are ignored.
    /// </summary>
    public static bool TryParse(string? text, out CalendarRecurrenceRule? rule) => TryParse(text, null, out rule);

    internal static bool TryParse(string? text, Func<ParsedDateTime, CalendarDateTime?>? until, out CalendarRecurrenceRule? rule) {
        rule = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        CalendarFrequency? frequency = null;
        int interval = 1;
        int? count = null;
        CalendarDateTime? untilValue = null;
        IReadOnlyList<int> bySecond = [], byMinute = [], byHour = [], byMonthDay = [], byYearDay = [], byWeekNo = [], byMonth = [], bySetPos = [];
        IReadOnlyList<CalendarWeekday> byDay = [];
        var weekStart = DayOfWeek.Monday;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var equals = part.IndexOf('=');
            if (equals <= 0) return false;
            var key = part[..equals].Trim().ToUpperInvariant();
            var value = part[(equals + 1)..].Trim();
            switch (key) {
                case "FREQ":
                    frequency = value.ToUpperInvariant() switch {
                        "SECONDLY" => CalendarFrequency.Secondly,
                        "MINUTELY" => CalendarFrequency.Minutely,
                        "HOURLY" => CalendarFrequency.Hourly,
                        "DAILY" => CalendarFrequency.Daily,
                        "WEEKLY" => CalendarFrequency.Weekly,
                        "MONTHLY" => CalendarFrequency.Monthly,
                        "YEARLY" => CalendarFrequency.Yearly,
                        _ => null,
                    };
                    if (frequency is null) return false;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out interval) || interval < 1) return false;
                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var c) || c < 1) return false;
                    count = c;
                    break;
                case "UNTIL":
                    if (!CalendarValues.TryParseDateTime(value, out var parsed)) return false;
                    untilValue = until?.Invoke(parsed) ?? Default(parsed);
                    break;
                case "BYSECOND" when TryList(value, 0, 60, false, out var list): bySecond = list; break;
                case "BYMINUTE" when TryList(value, 0, 59, false, out var list): byMinute = list; break;
                case "BYHOUR" when TryList(value, 0, 23, false, out var list): byHour = list; break;
                case "BYMONTHDAY" when TryList(value, 1, 31, true, out var list): byMonthDay = list; break;
                case "BYYEARDAY" when TryList(value, 1, 366, true, out var list): byYearDay = list; break;
                case "BYWEEKNO" when TryList(value, 1, 53, true, out var list): byWeekNo = list; break;
                case "BYMONTH" when TryList(value, 1, 12, false, out var list): byMonth = list; break;
                case "BYSETPOS" when TryList(value, 1, 366, true, out var list): bySetPos = list; break;
                case "BYDAY":
                    if (!TryDays(value, out byDay)) return false;
                    break;
                case "WKST":
                    if (!TryDay(value, out weekStart)) return false;
                    break;
                case "RSCALE":
                    if (!value.Equals("GREGORIAN", StringComparison.OrdinalIgnoreCase)) return false;
                    break;
                case "BYSECOND" or "BYMINUTE" or "BYHOUR" or "BYMONTHDAY" or "BYYEARDAY" or "BYWEEKNO" or "BYMONTH" or "BYSETPOS":
                    return false;
            }
        }

        if (frequency is null) return false;
        rule = new CalendarRecurrenceRule {
            Frequency = frequency.Value,
            Interval = interval,
            Count = count,
            Until = untilValue,
            BySecond = bySecond,
            ByMinute = byMinute,
            ByHour = byHour,
            ByDay = byDay,
            ByMonthDay = byMonthDay,
            ByYearDay = byYearDay,
            ByWeekNo = byWeekNo,
            ByMonth = byMonth,
            BySetPosition = bySetPos,
            WeekStart = weekStart,
        };
        return true;

        static CalendarDateTime Default(ParsedDateTime parsed) =>
            parsed.IsDate ? CalendarDateTime.FromDate(parsed.Date)
            : parsed.IsUtc ? CalendarDateTime.Utc(new DateTimeOffset(parsed.ToDateTime(), TimeSpan.Zero))
            : CalendarDateTime.Floating(parsed.ToDateTime());
    }

    /// <summary>The rule in its canonical form, the parts in a fixed order.</summary>
    public override string ToString() {
        var builder = new StringBuilder("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Until is { } until) {
            builder.Append(";UNTIL=").Append(until.Kind switch {
                CalendarDateTimeKind.Date => CalendarValues.FormatDate(until.Date),
                CalendarDateTimeKind.Utc => CalendarValues.FormatUtc(new DateTimeOffset(until.Value, TimeSpan.Zero)),
                CalendarDateTimeKind.Zoned => CalendarValues.FormatUtc(until.ToUtc()!.Value),
                _ => CalendarValues.FormatLocal(until.Value),
            });
        }

        if (Count is { } count) builder.Append(";COUNT=").Append(count.ToString(CultureInfo.InvariantCulture));
        if (Interval != 1) builder.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));
        Append(builder, "BYSECOND", BySecond);
        Append(builder, "BYMINUTE", ByMinute);
        Append(builder, "BYHOUR", ByHour);
        if (ByDay.Count > 0) builder.Append(";BYDAY=").Append(string.Join(',', ByDay));
        Append(builder, "BYMONTHDAY", ByMonthDay);
        Append(builder, "BYYEARDAY", ByYearDay);
        Append(builder, "BYWEEKNO", ByWeekNo);
        Append(builder, "BYMONTH", ByMonth);
        Append(builder, "BYSETPOS", BySetPosition);
        if (WeekStart != DayOfWeek.Monday) builder.Append(";WKST=").Append(DayCode(WeekStart));
        return builder.ToString();
    }

    /// <inheritdoc/>
    public bool Equals(CalendarRecurrenceRule? other) => other is not null && ToString() == other.ToString();

    /// <inheritdoc/>
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    internal static string DayCode(DayOfWeek day) => day switch {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    private static bool TryDay(ReadOnlySpan<char> text, out DayOfWeek day) {
        DayOfWeek? found = text.Length != 2 ? null
            : text.Equals("MO", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Monday
            : text.Equals("TU", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Tuesday
            : text.Equals("WE", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Wednesday
            : text.Equals("TH", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Thursday
            : text.Equals("FR", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Friday
            : text.Equals("SA", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Saturday
            : text.Equals("SU", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Sunday
            : null;
        day = found ?? DayOfWeek.Monday;
        return found is not null;
    }

    // A BYDAY list, without repeated values: at most the seven weekdays alone and at 2 × 53 positions each, however long
    // the text. The items are walked as spans, so a long list costs time linear in its length and no allocation per item.
    private static bool TryDays(string text, out IReadOnlyList<CalendarWeekday> values) {
        var list = new List<CalendarWeekday>();
        values = list;
        var distinct = new HashSet<CalendarWeekday>();
        var span = text.AsSpan();
        foreach (var range in span.Split(',')) {
            var item = span[range].Trim();
            if (item.IsEmpty) continue;
            if (item.Length < 2 || !TryDay(item[^2..], out var day)) return false;
            var offset = 0;
            if (item.Length > 2 && (!int.TryParse(item[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset)
                                    || offset == 0 || Math.Abs(offset) > 53)) return false;
            if (distinct.Add(new CalendarWeekday(day, offset))) list.Add(new CalendarWeekday(day, offset));
        }

        return true;
    }

    // A BY list, without repeated values: at most as many values as the part has, however long the text (RFC 5545 allows
    // repeats, BYMONTH=1,1,1 is January). The items are walked as spans, so a long list costs time linear in its length
    // and no allocation per item; the length itself is bounded by the reader's line limit.
    private static bool TryList(string text, int min, int max, bool allowNegative, out IReadOnlyList<int> values) {
        var list = new List<int>();
        values = list;
        var distinct = new HashSet<int>();
        var span = text.AsSpan();
        foreach (var range in span.Split(',')) {
            var item = span[range].Trim();
            if (item.IsEmpty) continue;
            if (!int.TryParse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return false;
            var magnitude = Math.Abs(value);
            if (value < 0 && !allowNegative) return false;
            if (magnitude < min || magnitude > max || (value == 0 && min > 0)) return false;
            if (distinct.Add(value)) list.Add(value);
        }

        return list.Count > 0;
    }

    private static void Append(StringBuilder builder, string name, IReadOnlyList<int> values) {
        if (values.Count == 0) return;
        builder.Append(';').Append(name).Append('=');
        for (var i = 0; i < values.Count; i++) {
            if (i > 0) builder.Append(',');
            builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }
    }
}
