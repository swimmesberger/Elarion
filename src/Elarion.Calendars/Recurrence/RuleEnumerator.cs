using Elarion.Calendars.TimeZones;

namespace Elarion.Calendars.Recurrence;

/// <summary>One instance of a rule: its wall-clock time, and its UTC instant when the series has a zone.</summary>
internal readonly record struct RecurrenceInstance(DateTime Local, DateTimeOffset? Utc);

/// <summary>
/// Enumerates the instances of one recurrence rule following RFC 5545 section 3.3.10 (spec 0004 FR-031): periods by
/// <c>FREQ</c> and <c>INTERVAL</c>, days and times expanded or limited by the <c>BY</c> parts, <c>BYSETPOS</c> per
/// period, then <c>COUNT</c> and <c>UNTIL</c>. Daily and longer frequencies compute on wall-clock times; sub-daily
/// frequencies of a zoned series step in elapsed time, so an hourly rule stays hourly across a daylight-saving change.
/// The <c>DTSTART</c> is the first instance and counts towards <c>COUNT</c>.
/// </summary>
/// <remarks>
/// The work is bounded three ways: periods without an instance (<c>maxIterations</c>), candidates built across all periods
/// and within one (<c>maxCandidates</c>, checked as each is built, so one period of a year's every second stops early),
/// and the cancellation token, checked per period. Without <c>COUNT</c>, enumeration starts a few periods before the
/// window, for every frequency.
/// </remarks>
internal sealed class RuleEnumerator {
    private readonly CalendarRecurrenceRule _rule;
    private readonly DateTime _start;
    private readonly bool _isDate;
    private readonly TimeZoneInfo? _zone;
    private readonly Func<RecurrenceInstance, bool> _beyondUntil;
    private readonly DateTime _stopLocal;
    private readonly DateTime? _skipBeforeLocal;
    private readonly int _maxIterations;
    private readonly long _maxCandidates;
    private readonly CancellationToken _cancellationToken;
    private readonly IReadOnlyList<TimeOnly> _times;
    private long _built;

    private readonly IReadOnlyList<int> _byMonthDay;
    private readonly IReadOnlyList<int> _byMonth;
    private readonly IReadOnlyList<CalendarWeekday> _byDay;

    public RuleEnumerator(
        CalendarRecurrenceRule rule,
        DateTime start,
        bool isDate,
        TimeZoneInfo? zone,
        Func<RecurrenceInstance, bool> beyondUntil,
        DateTime stopLocal,
        DateTime? skipBeforeLocal,
        int maxIterations,
        long maxCandidates = long.MaxValue,
        CancellationToken cancellationToken = default) {
        _rule = rule;
        _start = start;
        _isDate = isDate;
        _zone = zone;
        _beyondUntil = beyondUntil;
        _stopLocal = stopLocal;
        _skipBeforeLocal = rule.Count is null ? skipBeforeLocal : null;
        _maxIterations = maxIterations;
        _maxCandidates = maxCandidates;
        _cancellationToken = cancellationToken;
        // Sub-daily rules build their times per period and never read these.
        _times = isDate || rule.Frequency is CalendarFrequency.Hourly or CalendarFrequency.Minutely or CalendarFrequency.Secondly
            ? [TimeOnly.MinValue]
            : Times(rule, start);

        // The defaults RFC 5545 takes from DTSTART when no BY part constrains a level.
        _byMonthDay = rule.ByMonthDay;
        _byMonth = rule.ByMonth;
        _byDay = rule.ByDay;
        if (rule.ByWeekNo.Count == 0 && rule.ByYearDay.Count == 0 && rule.ByMonthDay.Count == 0 && rule.ByDay.Count == 0) {
            switch (rule.Frequency) {
                case CalendarFrequency.Yearly:
                    if (_byMonth.Count == 0) _byMonth = [start.Month];
                    _byMonthDay = [start.Day];
                    break;
                case CalendarFrequency.Monthly:
                    _byMonthDay = [start.Day];
                    break;
                case CalendarFrequency.Weekly:
                    _byDay = [new CalendarWeekday(start.DayOfWeek)];
                    break;
            }
        }
        else if (rule.ByWeekNo.Count > 0 && rule.ByDay.Count == 0 && rule.ByYearDay.Count == 0 && rule.ByMonthDay.Count == 0) {
            // A week number names seven days; the day within it comes from DTSTART, as RFC 5545 §3.3.10 derives what a
            // rule leaves open (sabre/vobject; python-dateutil expands the whole week instead).
            _byDay = [new CalendarWeekday(start.DayOfWeek)];
        }
    }

    /// <summary>Whether the enumeration stopped because no period produced an instance within the iteration cap.</summary>
    public bool HitIterationLimit { get; private set; }

    /// <summary>Whether the enumeration stopped because it built more candidates than its cap.</summary>
    public bool HitCandidateLimit { get; private set; }

    /// <summary>Whether the rule can never produce an instance after its start, such as February 30.</summary>
    public static bool IsImpossible(CalendarRecurrenceRule rule) {
        if (rule.ByMonthDay.Count == 0 || rule.ByMonth.Count == 0 || rule.ByYearDay.Count > 0 || rule.ByWeekNo.Count > 0) return false;
        foreach (var month in rule.ByMonth) {
            var max = month == 2 ? 29 : DateTime.DaysInMonth(2001, month);
            foreach (var day in rule.ByMonthDay)
                if (Math.Abs(day) <= max) return false;
        }

        return true;
    }

    public IEnumerable<RecurrenceInstance> Enumerate() {
        var count = 0;
        var first = new RecurrenceInstance(_start, _zone is null ? null : ZoneMath.ToUtc(_start, _zone));
        // RFC 5545 §3.8.5.3: DTSTART is always the first instance, even past an UNTIL that precedes it (sabre/vobject).
        yield return first;
        count++;
        if (_beyondUntil(first)) yield break;
        if (_rule.Count is { } maxCount && count >= maxCount) yield break;

        var subDaily = _rule.Frequency is CalendarFrequency.Hourly or CalendarFrequency.Minutely or CalendarFrequency.Secondly;
        var elapsed = subDaily && _zone is not null && !_isDate;
        var startUtc = first.Utc;
        var sinceLast = 0;
        var candidates = new List<RecurrenceInstance>();
        for (var k = FirstPeriod(); ; k++) {
            _cancellationToken.ThrowIfCancellationRequested();
            candidates.Clear();
            bool past;
            if (elapsed) past = !ElapsedPeriod(k, startUtc!.Value, candidates);
            else past = !WallPeriod(k, candidates);
            // A period cut short by the candidate cap is not used: its BYSETPOS positions would be wrong.
            if (past || HitCandidateLimit) yield break;

            var produced = false;
            foreach (var candidate in ApplySetPosition(candidates)) {
                if (candidate.Local <= _start && (candidate.Utc is null || startUtc is null || candidate.Utc <= startUtc)) continue;
                if (candidate.Local > _stopLocal && (candidate.Utc is null || ZoneMath.ToLocal(candidate.Utc.Value, _zone!) > _stopLocal))
                    yield break;
                if (_beyondUntil(candidate)) yield break;
                produced = true;
                yield return candidate;
                if (_rule.Count is { } limit && ++count >= limit) yield break;
            }

            if (produced) sinceLast = 0;
            else if (++sinceLast > _maxIterations) {
                HitIterationLimit = true;
                yield break;
            }
        }
    }

    private long FirstPeriod() {
        if (_skipBeforeLocal is not { } skip || skip <= _start) return 0;
        switch (_rule.Frequency) {
            case CalendarFrequency.Monthly:
                var months = (long)(skip.Year - _start.Year) * 12 + skip.Month - _start.Month;
                return Math.Max(0, months / _rule.Interval - 1);
            case CalendarFrequency.Yearly:
                // A year's week-numbered days can begin in the previous December: one period of margin.
                return Math.Max(0, (skip.Year - _start.Year) / _rule.Interval - 1);
        }

        long unitTicks = _rule.Frequency switch {
            CalendarFrequency.Secondly => TimeSpan.TicksPerSecond,
            CalendarFrequency.Minutely => TimeSpan.TicksPerMinute,
            CalendarFrequency.Hourly => TimeSpan.TicksPerHour,
            CalendarFrequency.Daily => TimeSpan.TicksPerDay,
            CalendarFrequency.Weekly => TimeSpan.TicksPerDay * 7,
            _ => 0,
        };
        if (unitTicks == 0) return 0;
        var periods = (skip - _start).Ticks / (unitTicks * _rule.Interval);
        return Math.Max(0, periods - 2);
    }

    // Fills the candidates of wall-clock period k; false when the period lies past the stop.
    private bool WallPeriod(long k, List<RecurrenceInstance> candidates) {
        var step = k * _rule.Interval;
        switch (_rule.Frequency) {
            case CalendarFrequency.Yearly: {
                var year = _start.Year + step;
                if (year > 9999 || new DateTime((int)year, 1, 1) > _stopLocal) return false;
                AddDays(YearDays((int)year), (int)year, null, candidates);
                return true;
            }
            case CalendarFrequency.Monthly: {
                var months = (long)_start.Year * 12 + _start.Month - 1 + step;
                var year = months / 12;
                if (year > 9999) return false;
                var month = (int)(months % 12) + 1;
                var first = new DateOnly((int)year, month, 1);
                if (first.ToDateTime(TimeOnly.MinValue) > _stopLocal) return false;
                var days = new List<DateOnly>(31);
                for (var day = first; day.Month == month; day = day.AddDays(1)) {
                    days.Add(day);
                    if (day == DateOnly.MaxValue) break;
                }

                AddDays(days, (int)year, month, candidates);
                return true;
            }
            case CalendarFrequency.Weekly: {
                var startDate = DateOnly.FromDateTime(_start);
                var weekStart = startDate.DayNumber - ((int)startDate.DayOfWeek - (int)_rule.WeekStart + 7) % 7;
                var dayNumber = weekStart + step * 7;
                if (dayNumber > DateOnly.MaxValue.DayNumber - 7) return false;
                var first = DateOnly.FromDayNumber((int)dayNumber);
                if (first.ToDateTime(TimeOnly.MinValue) > _stopLocal) return false;
                var days = new List<DateOnly>(7);
                for (var i = 0; i < 7; i++) days.Add(first.AddDays(i));
                AddDays(days, first.Year, null, candidates);
                return true;
            }
            case CalendarFrequency.Daily: {
                var dayNumber = DateOnly.FromDateTime(_start).DayNumber + step;
                if (dayNumber > DateOnly.MaxValue.DayNumber) return false;
                var day = DateOnly.FromDayNumber((int)dayNumber);
                if (day.ToDateTime(TimeOnly.MinValue) > _stopLocal) return false;
                AddDays([day], day.Year, null, candidates);
                return true;
            }
            default: {
                var unit = UnitTicks();
                var ticks = _start.Ticks + step * unit;
                if (ticks > DateTime.MaxValue.Ticks) return false;
                var period = new DateTime(ticks);
                if (period > _stopLocal) return false;
                AddSubDaily(period, null, candidates);
                return true;
            }
        }
    }

    // Fills the candidates of elapsed-time period k of a zoned sub-daily rule.
    private bool ElapsedPeriod(long k, DateTimeOffset startUtc, List<RecurrenceInstance> candidates) {
        var ticks = startUtc.UtcTicks + k * _rule.Interval * UnitTicks();
        if (ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay) return false;
        var periodUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        var local = ZoneMath.ToLocal(periodUtc, _zone!);
        if (local > _stopLocal) return false;
        AddSubDaily(local, periodUtc, candidates);
        return true;
    }

    private long UnitTicks() => _rule.Frequency switch {
        CalendarFrequency.Hourly => TimeSpan.TicksPerHour,
        CalendarFrequency.Minutely => TimeSpan.TicksPerMinute,
        _ => TimeSpan.TicksPerSecond,
    };

    private void AddSubDaily(DateTime local, DateTimeOffset? periodUtc, List<RecurrenceInstance> candidates) {
        var date = DateOnly.FromDateTime(local);
        if (!MatchesDay(date, date.Year, null, ignoreOffsets: true)) return;
        if (_rule.ByHour.Count > 0 && !_rule.ByHour.Contains(local.Hour)) return;
        IReadOnlyList<int> minutes, seconds;
        switch (_rule.Frequency) {
            case CalendarFrequency.Hourly:
                minutes = _rule.ByMinute.Count > 0 ? _rule.ByMinute : [_start.Minute];
                seconds = _rule.BySecond.Count > 0 ? _rule.BySecond : [_start.Second];
                break;
            case CalendarFrequency.Minutely:
                if (_rule.ByMinute.Count > 0 && !_rule.ByMinute.Contains(local.Minute)) return;
                minutes = [local.Minute];
                seconds = _rule.BySecond.Count > 0 ? _rule.BySecond : [_start.Second];
                break;
            default:
                if (_rule.ByMinute.Count > 0 && !_rule.ByMinute.Contains(local.Minute)) return;
                if (_rule.BySecond.Count > 0 && !_rule.BySecond.Contains(local.Second)) return;
                minutes = [local.Minute];
                seconds = [local.Second];
                break;
        }

        foreach (var minute in minutes.Order()) {
            foreach (var second in seconds.Order()) {
                if (!Built()) return;
                var s = Math.Min(second, 59);
                var wall = new DateTime(local.Year, local.Month, local.Day, local.Hour, minute, s);
                DateTimeOffset? utc = null;
                if (periodUtc is { } p) utc = p + (new TimeSpan(0, minute, s) - new TimeSpan(0, local.Minute, local.Second));
                candidates.Add(new RecurrenceInstance(wall, utc));
            }
        }
    }

    // Counts one more candidate; false, and the cap marked, when it is past the cap.
    private bool Built() {
        if (++_built <= _maxCandidates) return true;
        HitCandidateLimit = true;
        return false;
    }

    // The times of day of a daily or longer rule, sorted; the same for every period, so built once.
    private static List<TimeOnly> Times(CalendarRecurrenceRule rule, DateTime start) {
        var hours = rule.ByHour.Count > 0 ? rule.ByHour : [start.Hour];
        var minutes = rule.ByMinute.Count > 0 ? rule.ByMinute : [start.Minute];
        var seconds = rule.BySecond.Count > 0 ? rule.BySecond : [start.Second];
        var list = new List<TimeOnly>(hours.Count * minutes.Count * seconds.Count);
        foreach (var h in hours)
        foreach (var m in minutes)
        foreach (var s in seconds)
            list.Add(new TimeOnly(h, m, Math.Min(s, 59)));
        list.Sort();
        return list;
    }

    private void AddDays(IEnumerable<DateOnly> days, int periodYear, int? periodMonth, List<RecurrenceInstance> candidates) {
        foreach (var day in days) {
            if (!MatchesDay(day, periodYear, periodMonth, ignoreOffsets: false)) continue;
            foreach (var time in _times) {
                if (!Built()) return;
                var local = day.ToDateTime(time);
                candidates.Add(new RecurrenceInstance(local, _zone is null ? null : ZoneMath.ToUtc(local, _zone)));
            }
        }
    }

    private IEnumerable<RecurrenceInstance> ApplySetPosition(List<RecurrenceInstance> candidates) {
        if (_rule.BySetPosition.Count == 0) return candidates;
        var selected = new SortedSet<int>();
        foreach (var position in _rule.BySetPosition) {
            var index = position > 0 ? position - 1 : candidates.Count + position;
            if (index >= 0 && index < candidates.Count) selected.Add(index);
        }

        return selected.Select(index => candidates[index]);
    }

    private IEnumerable<DateOnly> YearDays(int year) {
        if (_rule.ByWeekNo.Count > 0) {
            var week1 = FirstWeekStart(year);
            var weeks = (FirstWeekStart(year + 1).DayNumber - week1.DayNumber) / 7;
            var days = new SortedSet<DateOnly>();
            foreach (var number in _rule.ByWeekNo) {
                var week = number > 0 ? number : weeks + number + 1;
                if (week < 1 || week > weeks) continue;
                var startDay = week1.AddDays((week - 1) * 7);
                for (var i = 0; i < 7; i++) days.Add(startDay.AddDays(i));
            }

            return days;
        }

        var months = _byMonth.Count > 0 ? _byMonth.Order().ToArray() : [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        return months.Distinct().SelectMany(month => Enumerable.Range(1, DateTime.DaysInMonth(year, month))
            .Select(day => new DateOnly(year, month, day)));
    }

    private DateOnly FirstWeekStart(int year) {
        if (year > 9999) return DateOnly.MaxValue.AddDays(-7);
        var jan1 = new DateOnly(year, 1, 1);
        var delta = ((int)jan1.DayOfWeek - (int)_rule.WeekStart + 7) % 7;
        var weekStart = jan1.DayNumber - delta;
        return DateOnly.FromDayNumber(7 - delta >= 4 ? weekStart : weekStart + 7);
    }

    private bool MatchesDay(DateOnly day, int periodYear, int? periodMonth, bool ignoreOffsets) {
        if (_byMonth.Count > 0 && !_byMonth.Contains(day.Month)) return false;
        if (_rule.ByYearDay.Count > 0) {
            var yearLength = DateTime.IsLeapYear(day.Year) ? 366 : 365;
            var dayOfYear = day.DayOfYear;
            var matched = false;
            foreach (var value in _rule.ByYearDay)
                if (value == dayOfYear || value == dayOfYear - yearLength - 1) {
                    matched = true;
                    break;
                }

            if (!matched) return false;
        }

        if (_byMonthDay.Count > 0) {
            var monthLength = DateTime.DaysInMonth(day.Year, day.Month);
            var matched = false;
            foreach (var value in _byMonthDay)
                if (value == day.Day || value == day.Day - monthLength - 1) {
                    matched = true;
                    break;
                }

            if (!matched) return false;
        }

        if (_byDay.Count > 0) {
            var matched = false;
            foreach (var weekday in _byDay) {
                if (weekday.DayOfWeek != day.DayOfWeek) continue;
                if (weekday.Offset == 0 || ignoreOffsets || _rule.ByWeekNo.Count > 0
                    || _rule.Frequency is not (CalendarFrequency.Monthly or CalendarFrequency.Yearly)) {
                    matched = true;
                    break;
                }

                // A position within the month for a monthly rule or a yearly rule with BYMONTH, else within the year.
                int index, total;
                if (_rule.Frequency == CalendarFrequency.Monthly || _rule.ByMonth.Count > 0) {
                    index = day.Day;
                    total = DateTime.DaysInMonth(day.Year, day.Month);
                }
                else {
                    index = day.DayOfYear;
                    total = DateTime.IsLeapYear(day.Year) ? 366 : 365;
                }

                var position = weekday.Offset > 0 ? (index - 1) / 7 + 1 : -((total - index) / 7 + 1);
                if (position == weekday.Offset) {
                    matched = true;
                    break;
                }
            }

            if (!matched) return false;
        }

        _ = periodYear;
        _ = periodMonth;
        return true;
    }
}
