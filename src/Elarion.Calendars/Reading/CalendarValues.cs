using System.Globalization;
using System.Text;

namespace Elarion.Calendars.Reading;

/// <summary>A date-time value as written, before its zone is resolved.</summary>
internal readonly record struct ParsedDateTime(DateOnly Date, TimeOnly? Time, bool IsUtc) {
    public bool IsDate => Time is null;

    public DateTime ToDateTime() => Date.ToDateTime(Time ?? TimeOnly.MinValue);
}

/// <summary>
/// The value parsers and formatters of iCalendar (RFC 5545 section 3.3), with the repairs of spec 0004 FR-017 in the
/// one date-time parser every date-time value goes through.
/// </summary>
internal static class CalendarValues {
    /// <summary>
    /// Parses a date or a date-time. Repairs: a missing seconds part, a value truncated after <c>T</c> (midnight), a
    /// repeated time suffix (<c>T000000ZT000000Z</c>), and the extended ISO form with <c>-</c> and <c>:</c>.
    /// </summary>
    public static bool TryParseDateTime(ReadOnlySpan<char> text, out ParsedDateTime value) {
        value = default;
        text = text.Trim();
        if (text.Length < 8) return false;

        Span<char> buffer = stackalloc char[Math.Min(text.Length, 64)];
        var length = 0;
        var tCount = 0;
        foreach (var c in text) {
            if (c is '-' or ':') continue;
            if (c is 'T' or 't') {
                if (++tCount == 2) break;
            }

            if (length == buffer.Length) return false;
            buffer[length++] = char.ToUpperInvariant(c);
        }

        var s = buffer[..length];
        if (!TryDigits(s[..4], out var year) || !TryDigits(s.Slice(4, 2), out var month) || !TryDigits(s.Slice(6, 2), out var day))
            return false;
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        var date = new DateOnly(year, month, day);
        if (s.Length == 8) {
            value = new ParsedDateTime(date, null, false);
            return true;
        }

        if (s[8] != 'T') return false;
        var time = s[9..];
        var utc = false;
        if (time.Length > 0 && time[^1] == 'Z') {
            utc = true;
            time = time[..^1];
        }

        var dot = time.IndexOfAny('.', ',');
        if (dot >= 0) time = time[..dot];
        int hour = 0, minute = 0, second = 0;
        switch (time.Length) {
            case 0: break;
            case 2:
                if (!TryDigits(time, out hour)) return false;
                break;
            case 4:
                if (!TryDigits(time[..2], out hour) || !TryDigits(time[2..], out minute)) return false;
                break;
            case 6:
                if (!TryDigits(time[..2], out hour) || !TryDigits(time.Slice(2, 2), out minute) || !TryDigits(time[4..], out second))
                    return false;
                break;
            default: return false;
        }

        if (second == 60) second = 59;
        if (hour == 24 && minute == 0 && second == 0) {
            if (date == DateOnly.MaxValue) return false;
            date = date.AddDays(1);
            hour = 0;
        }

        if (hour > 23 || minute > 59 || second > 59) return false;
        value = new ParsedDateTime(date, new TimeOnly(hour, minute, second), utc);
        return true;
    }

    /// <summary>Parses a duration (<c>[+-]P[nW]</c> or <c>[+-]P[nD][T[nH][nM][nS]]</c>), leniently in any case.</summary>
    public static bool TryParseDuration(ReadOnlySpan<char> text, out TimeSpan value) {
        value = default;
        text = text.Trim();
        if (text.IsEmpty) return false;
        var negative = false;
        if (text[0] is '+' or '-') {
            negative = text[0] == '-';
            text = text[1..];
        }

        if (text.IsEmpty || char.ToUpperInvariant(text[0]) != 'P') return false;
        text = text[1..];
        long ticks = 0;
        var inTime = false;
        var any = false;
        while (!text.IsEmpty) {
            if (char.ToUpperInvariant(text[0]) == 'T') {
                if (inTime) return false;
                inTime = true;
                text = text[1..];
                continue;
            }

            var digits = 0;
            while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
            if (digits == 0 || digits == text.Length || digits > 9) return false;
            var number = long.Parse(text[..digits], NumberStyles.None, CultureInfo.InvariantCulture);
            var unit = char.ToUpperInvariant(text[digits]);
            text = text[(digits + 1)..];
            any = true;
            ticks += (unit, inTime) switch {
                ('W', false) => number * TimeSpan.TicksPerDay * 7,
                ('D', false) => number * TimeSpan.TicksPerDay,
                ('H', true) => number * TimeSpan.TicksPerHour,
                ('M', true) => number * TimeSpan.TicksPerMinute,
                ('S', true) => number * TimeSpan.TicksPerSecond,
                _ => long.MinValue,
            };
            if (ticks < 0) return false;
        }

        if (!any) return false;
        value = TimeSpan.FromTicks(negative ? -ticks : ticks);
        return true;
    }

    /// <summary>Formats a duration in its shortest RFC 5545 form, such as <c>PT1H</c>, <c>P1D</c>, <c>-PT15M</c> or <c>P2W</c>.</summary>
    public static string FormatDuration(TimeSpan value) {
        var builder = new StringBuilder();
        if (value < TimeSpan.Zero) {
            builder.Append('-');
            value = value.Duration();
        }

        builder.Append('P');
        var seconds = (long)value.TotalSeconds;
        if (seconds == 0) return builder.Append("T0S").ToString();
        var days = seconds / 86_400;
        var rest = seconds % 86_400;
        if (rest == 0 && days % 7 == 0) return builder.Append((days / 7).ToString(CultureInfo.InvariantCulture)).Append('W').ToString();
        if (days > 0) builder.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        if (rest > 0) {
            builder.Append('T');
            var hours = rest / 3600;
            var minutes = rest % 3600 / 60;
            var secs = rest % 60;
            if (hours > 0) builder.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            if (minutes > 0) builder.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            if (secs > 0) builder.Append(secs.ToString(CultureInfo.InvariantCulture)).Append('S');
        }

        return builder.ToString();
    }

    /// <summary>Parses a UTC offset, <c>+hhmm</c> or <c>+hhmmss</c>, leniently with a colon.</summary>
    public static bool TryParseUtcOffset(ReadOnlySpan<char> text, out TimeSpan value) {
        value = default;
        text = text.Trim();
        if (text.Length < 5 || text[0] is not ('+' or '-')) return false;
        var negative = text[0] == '-';
        Span<char> digits = stackalloc char[6];
        var count = 0;
        foreach (var c in text[1..]) {
            if (c == ':') continue;
            if (!char.IsAsciiDigit(c) || count == 6) return false;
            digits[count++] = c;
        }

        if (count is not (4 or 6)) return false;
        var hours = (digits[0] - '0') * 10 + digits[1] - '0';
        var minutes = (digits[2] - '0') * 10 + digits[3] - '0';
        var seconds = count == 6 ? (digits[4] - '0') * 10 + digits[5] - '0' : 0;
        if (hours > 23 || minutes > 59 || seconds > 59) return false;
        value = new TimeSpan(hours, minutes, seconds);
        if (negative) value = -value;
        return true;
    }

    /// <summary>Formats a UTC offset as <c>+hhmm</c>, or <c>+hhmmss</c> when it has seconds.</summary>
    public static string FormatUtcOffset(TimeSpan offset) {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        var text = sign + offset.Hours.ToString("00", CultureInfo.InvariantCulture) + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
        return offset.Seconds == 0 ? text : text + offset.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    public static bool TryParseInteger(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    public static string FormatDate(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string FormatLocal(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    public static string FormatUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static bool TryDigits(ReadOnlySpan<char> text, out int value) {
        value = 0;
        if (text.IsEmpty) return false;
        foreach (var c in text) {
            if (!char.IsAsciiDigit(c)) return false;
            value = value * 10 + (c - '0');
        }

        return true;
    }
}
