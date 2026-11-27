using System.Collections.Concurrent;
using System.Globalization;
using Elarion.Calendars.Reading;
using Elarion.Calendars.Recurrence;

namespace Elarion.Calendars;

/// <summary>
/// Time zones of iCalendar files (spec 0004 FR-025–FR-027, FR-050, FR-051): <c>TZID</c> normalization and resolution
/// through the system, <c>VTIMEZONE</c> to <see cref="TimeZoneInfo"/>, and <see cref="TimeZoneInfo"/> to
/// <c>VTIMEZONE</c>.
/// </summary>
/// <remarks>
/// Resolution depends on the system's time-zone data, which slim containers lack; install <c>tzdata</c> there.
/// </remarks>
public static class CalendarTimeZones {
    private const int FirstYear = 1970;
    private const int LastYear = 2100;
    // The trailing segments of a vendor-prefixed id tried as an id: an IANA id has at most three.
    private const int MaxSuffixSegments = 4;

    // The longest id the system can know: .NET refuses longer ones without a lookup.
    private const int MaxSystemIdLength = 255;

    // The most entries of each process-wide cache. Only zones the system knows are cached, so real ids never reach the
    // bound; it stops case variants of known ids from growing a cache without end.
    private const int MaxCacheEntries = 1_024;

    // Zones the system resolved, keyed by the candidate that resolved. Misses are never cached, so an id from an
    // untrusted file leaves nothing behind (ADR-0109, amendment 2026-11-27).
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> SystemCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CalendarComponent> ComponentCache = new(StringComparer.Ordinal);

    /// <summary>The entries of both process-wide caches, for the tests that pin their bound.</summary>
    internal static int CachedEntryCount => SystemCache.Count + ComponentCache.Count;

    /// <summary>
    /// Resolves <paramref name="tzid"/> through the system's zones, as an IANA or a Windows id, after normalizing it:
    /// surrounding quotes and spaces, a vendor prefix ending in an IANA id (<c>/mozilla.org/20070129_1/Europe/Berlin</c>)
    /// and a <c>(UTC+01:00) </c> display prefix are removed; <c>UTC</c>, <c>GMT</c>, <c>Etc/UTC</c> and <c>Z</c> are UTC.
    /// </summary>
    /// <remarks>
    /// The work is linear in the id's length, and only zones the system knows are cached, so an id from an untrusted
    /// file costs neither quadratic time nor memory that outlives the call. A read refuses longer ids before they get
    /// here (<see cref="CalendarReadOptions.MaxTimeZoneIdLength"/>).
    /// </remarks>
    public static bool TryResolve(string tzid, out TimeZoneInfo zone) {
        ArgumentNullException.ThrowIfNull(tzid);
        var found = ResolveSystem(tzid);
        zone = found!;
        return found is not null;
    }

    /// <summary>The id <paramref name="tzid"/> normalizes to, before it is looked up.</summary>
    public static string Normalize(string tzid) {
        ArgumentNullException.ThrowIfNull(tzid);
        foreach (var candidate in Candidates(tzid))
            if (FindSystem(candidate) is not null) return candidate;

        return StripDisplayPrefix(tzid.Trim().Trim('"').Trim());
    }

    /// <summary>
    /// The <c>TZID</c> the writer uses for <paramref name="zone"/> (FR-050): its IANA id where the system can name one,
    /// otherwise its id.
    /// </summary>
    public static string GetWrittenId(TimeZoneInfo zone) {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone == TimeZoneInfo.Utc) return "UTC";
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    /// <summary>Whether the writer names <paramref name="zone"/> with an IANA id, so a reader can resolve it without a <c>VTIMEZONE</c>.</summary>
    public static bool HasIanaId(TimeZoneInfo zone) {
        ArgumentNullException.ThrowIfNull(zone);
        return zone.HasIanaId || TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out _);
    }

    internal static string WrittenId(CalendarDateTime value) =>
        value.TimeZone is { } zone ? GetWrittenId(zone) : value.TimeZoneId ?? "UTC";

    /// <summary>
    /// A <c>VTIMEZONE</c> whose observances reproduce <paramref name="zone"/>'s offsets from 1970 on (FR-051): runs of
    /// years that follow one rule are one recurring observance, and the run that reaches 2100 is left open. The result
    /// is cached per zone and must not be changed; clone it to change it.
    /// </summary>
    public static CalendarComponent ToComponent(TimeZoneInfo zone) {
        ArgumentNullException.ThrowIfNull(zone);
        var id = GetWrittenId(zone);
        var key = id + "\n" + zone.BaseUtcOffset.Ticks.ToString(CultureInfo.InvariantCulture);
        if (ComponentCache.TryGetValue(key, out var cached)) return cached;
        var component = BuildComponent(zone, id);
        // Only zones the system knows are cached: a zone built from an uploaded file's VTIMEZONE carries an id the file
        // chose, and caching it would keep every upload's ids for the life of the process.
        if (ComponentCache.Count < MaxCacheEntries && FindSystem(zone.Id) is { } system && system.HasSameRules(zone))
            component = ComponentCache.GetOrAdd(key, component);
        return component;
    }

    /// <summary>
    /// Converts a <c>VTIMEZONE</c> to a <see cref="TimeZoneInfo"/> whose id is its <c>TZID</c>, expanding its
    /// observances through 2100. <see langword="false"/> with an error when it defines no usable observance.
    /// </summary>
    public static bool TryFromComponent(CalendarComponent vtimezone, out TimeZoneInfo zone, out CalendarError? error) {
        ArgumentNullException.ThrowIfNull(vtimezone);
        zone = null!;
        error = null;
        var tzid = vtimezone.FindProperty("TZID")?.GetText().Trim() ?? "";
        if (tzid.Length == 0) {
            error = new CalendarError(CalendarErrorCodes.Required, "The time zone has no TZID.") { Property = "TZID", Component = "VTIMEZONE" };
            return false;
        }

        var transitions = new List<Transition>();
        foreach (var observance in vtimezone.Components) {
            if (observance.Name is not ("STANDARD" or "DAYLIGHT")) continue;
            if (!TryReadObservance(observance, transitions)) continue;
        }

        if (transitions.Count == 0) {
            error = new CalendarError(CalendarErrorCodes.UnknownTimeZone, $"The time zone '{tzid}' defines no observance.") {
                TimeZoneId = tzid,
                Component = "VTIMEZONE",
            };
            return false;
        }

        transitions.Sort(static (a, b) => a.Utc.CompareTo(b.Utc));
        zone = BuildZone(tzid, transitions);
        return true;
    }

    // ---- resolution -------------------------------------------------------------------------------------------------

    private static TimeZoneInfo? ResolveSystem(string tzid) {
        foreach (var candidate in Candidates(tzid)) {
            if (candidate.Length == 0) continue;
            if (candidate is "UTC" or "GMT" or "Etc/UTC" or "Etc/GMT" or "Z" or "Etc/Zulu" or "Zulu" or "UCT"
                || candidate.Equals("utc", StringComparison.OrdinalIgnoreCase))
                return TimeZoneInfo.Utc;
            if (FindSystem(candidate) is { } zone) return zone;
        }

        return null;
    }

    private static TimeZoneInfo? FindSystem(string candidate) {
        if (candidate.Length is 0 or > MaxSystemIdLength) return null;
        if (SystemCache.TryGetValue(candidate, out var cached)) return cached;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone)) return null;
        if (SystemCache.Count < MaxCacheEntries) SystemCache.TryAdd(candidate, zone);
        return zone;
    }

    // The ids tried for a TZID, in order: as written, without a display prefix, and for a vendor-prefixed id
    // (/mozilla.org/20070129_1/Europe/Berlin) its last few trailing segments, longest first. The suffixes are sliced
    // while scanning backwards, so the work is linear in the id's length however many segments it has.
    private static List<string> Candidates(string tzid) {
        var trimmed = tzid.Trim().Trim('"').Trim();
        var candidates = new List<string>(2 + MaxSuffixSegments) { trimmed };
        var stripped = StripDisplayPrefix(trimmed);
        if (stripped != trimmed) candidates.Add(stripped);
        if (!trimmed.StartsWith('/')) return candidates;

        var suffixes = candidates.Count;
        var end = trimmed.Length;
        while (candidates.Count - suffixes < MaxSuffixSegments && end > 0) {
            var slash = trimmed.LastIndexOf('/', end - 1);
            // The first segment is no candidate of its own: the id without its leading slash is what was tried already.
            if (slash <= 0) break;
            if (slash + 1 < end) candidates.Add(trimmed[(slash + 1)..]);
            end = slash;
        }

        candidates.Reverse(suffixes, candidates.Count - suffixes);
        return candidates;
    }

    private static string StripDisplayPrefix(string tzid) {
        if (!(tzid.StartsWith("(UTC", StringComparison.OrdinalIgnoreCase) || tzid.StartsWith("(GMT", StringComparison.OrdinalIgnoreCase)))
            return tzid;
        var close = tzid.IndexOf(')');
        return close < 0 ? tzid : tzid[(close + 1)..].Trim();
    }

    // ---- VTIMEZONE to TimeZoneInfo ----------------------------------------------------------------------------------

    private readonly record struct Transition(DateTime Utc, TimeSpan From, TimeSpan To, bool Daylight);

    private static bool TryReadObservance(CalendarComponent observance, List<Transition> transitions) {
        var start = observance.FindProperty("DTSTART");
        var fromProperty = observance.FindProperty("TZOFFSETFROM");
        var toProperty = observance.FindProperty("TZOFFSETTO");
        if (start is null || toProperty is null || !CalendarValues.TryParseDateTime(start.RawValue, out var parsedStart)) return false;
        if (!CalendarValues.TryParseUtcOffset(toProperty.RawValue, out var to)) return false;
        var from = fromProperty is not null && CalendarValues.TryParseUtcOffset(fromProperty.RawValue, out var parsedFrom) ? parsedFrom : to;
        var daylight = observance.Name == "DAYLIGHT";
        var local = parsedStart.ToDateTime();
        var stop = new DateTime(LastYear, 12, 31, 23, 59, 59);
        var onsets = new SortedSet<DateTime> { local };

        foreach (var rruleProperty in observance.FindProperties("RRULE")) {
            if (!CalendarRecurrenceRule.TryParse(rruleProperty.RawValue, out var rule)) continue;
            var until = rule!.Until;
            var enumerator = new RuleEnumerator(rule, local, isDate: false, zone: null,
                instance => until is { } u && (u.Kind == CalendarDateTimeKind.Utc ? instance.Local - from > u.Value : instance.Local > u.Value),
                stop, null, maxIterations: 1_000, maxCandidates: 100_000);
            var count = 0;
            foreach (var instance in enumerator.Enumerate()) {
                onsets.Add(instance.Local);
                if (++count > 2_000) break;
            }
        }

        foreach (var rdate in observance.FindProperties("RDATE"))
            foreach (var part in rdate.RawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (CalendarValues.TryParseDateTime(part.Split('/')[0], out var parsed)) onsets.Add(parsed.ToDateTime());

        foreach (var onset in onsets) transitions.Add(new Transition(onset - from, from, to, daylight));
        return true;
    }

    private static TimeZoneInfo BuildZone(string tzid, List<Transition> transitions) {
        var baseOffset = transitions.LastOrDefault(static t => !t.Daylight) is { To: var standardTo } last && last != default
            ? standardTo
            : transitions[^1].To;
        try {
            var rules = BuildRules(transitions, baseOffset);
            return TimeZoneInfo.CreateCustomTimeZone(tzid, baseOffset, tzid, tzid, tzid, rules);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidTimeZoneException) {
            // AdjustmentRule refuses definitions it cannot represent; the zone keeps its standard offset.
            return TimeZoneInfo.CreateCustomTimeZone(tzid, baseOffset, tzid, tzid);
        }
    }

    // One rule per calendar year, in the shape Windows time-zone data has: the year's daylight start and end as fixed
    // dates, a start after the end for a southern-hemisphere year (daylight time at both ends of the year), and the
    // year's standard offset as the base offset's delta. TimeZoneInfo evaluates rules per year, so this is the shape
    // its rule engine handles without seams.
    private static TimeZoneInfo.AdjustmentRule[] BuildRules(List<Transition> transitions, TimeSpan baseOffset) {
        var rules = new List<TimeZoneInfo.AdjustmentRule>();
        var yearStartMarker = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 1, 1);
        var yearEndMarker = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 59, 999), 12, 31);
        var index = 0;
        var state = new Transition(DateTime.MinValue, transitions[0].From, transitions[0].From, false);
        for (var year = Math.Max(1, transitions[0].Utc.Year); year <= LastYear; year++) {
            var yearStart = new DateTime(year, 1, 1);
            var nextYear = yearStart.AddYears(1);
            var inYear = new List<Transition>();
            while (index < transitions.Count && transitions[index].Utc + transitions[index].From < nextYear) {
                if (transitions[index].Utc + transitions[index].From >= yearStart) inYear.Add(transitions[index]);
                else state = transitions[index];
                index++;
            }

            // The year as segments of one offset each: from New Year, then from each transition.
            var segments = new List<(DateTime Start, TimeSpan Offset, bool Daylight)> { (yearStart, state.To, state.Daylight && state.To != state.From) };
            foreach (var t in inYear) segments.Add((t.Utc + t.From, t.To, t.Daylight && t.To != t.From));
            if (inYear.Count > 0) state = inYear[^1];

            // The standard offset is the last one not marked daylight; the other offset is shown as daylight time,
            // preferring a marked daylight offset when the year has more than two.
            var lastStandard = segments.LastOrDefault(static x => !x.Daylight);
            var standard = lastStandard != default ? lastStandard.Offset : segments[0].Offset;
            var others = Enumerable.Range(0, segments.Count).Where(i => segments[i].Offset != standard).ToList();
            var baseDelta = standard - baseOffset;
            var dateEnd = new DateTime(year, 12, 31);
            if (others.Count == 0) {
                if (baseDelta != TimeSpan.Zero)
                    rules.Add(TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(yearStart, dateEnd, TimeSpan.Zero, yearStartMarker,
                        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 1), 1, 1), baseDelta));
                continue;
            }

            var other = others.FirstOrDefault(i => segments[i].Daylight, others[0]);
            var daylightOffset = segments[other].Offset;
            var spans = others.Where(i => segments[i].Offset == daylightOffset).ToList();
            var first = spans[0];
            var last = spans[^1];
            var atStart = first == 0;
            var atEnd = last == segments.Count - 1;
            DateTime EndOf(int i) => segments[i + 1].Start - TimeSpan.FromMilliseconds(1);

            TimeZoneInfo.TransitionTime startTransition, endTransition;
            if (atStart && atEnd && spans.Count == 1) {
                (startTransition, endTransition) = (yearStartMarker, yearEndMarker);
            }
            else if (atStart && atEnd) {
                // Southern hemisphere: daylight time at both ends of the year.
                (startTransition, endTransition) = (Fixed(segments[last].Start), Fixed(EndOf(first)));
            }
            else if (atStart) {
                (startTransition, endTransition) = (yearStartMarker, Fixed(EndOf(last)));
            }
            else if (atEnd) {
                (startTransition, endTransition) = (Fixed(segments[first].Start), yearEndMarker);
            }
            else {
                (startTransition, endTransition) = (Fixed(segments[first].Start), Fixed(EndOf(last)));
            }

            rules.Add(TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(yearStart, dateEnd, daylightOffset - standard, startTransition,
                endTransition, baseDelta));
        }

        return [.. rules];

        static TimeZoneInfo.TransitionTime Fixed(DateTime local) =>
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1).Add(local.TimeOfDay), local.Month, local.Day);
    }

    // ---- TimeZoneInfo to VTIMEZONE ----------------------------------------------------------------------------------

    private readonly record struct Onset(DateTime Utc, TimeSpan From, TimeSpan To, bool Daylight) {
        public DateTime Local => Utc + From;
    }

    private static CalendarComponent BuildComponent(TimeZoneInfo zone, string id) {
        var onsets = FindOnsets(zone);
        var component = new CalendarComponent("VTIMEZONE");
        component.Properties.Add(CalendarProperty.Text("TZID", id));

        var start = new DateTime(FirstYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var initial = zone.GetUtcOffset(start);
        var observances = new List<(DateTime First, CalendarComponent Component)> {
            (new DateTime(FirstYear, 1, 1), Observance(zone.IsDaylightSavingTime(start), new DateTime(FirstYear, 1, 1), initial, initial, null)),
        };

        foreach (var group in onsets.GroupBy(static o => o.Daylight)) {
            var list = group.ToList();
            var i = 0;
            while (i < list.Count) {
                var key = RuleKey(list[i]);
                var j = i + 1;
                while (j < list.Count && list[j].Local.Year == list[j - 1].Local.Year + 1 && RuleKey(list[j]) == key) j++;
                var first = list[i];
                var last = list[j - 1];
                string? rrule = null;
                if (j - i > 1) {
                    rrule = "FREQ=YEARLY;BYMONTH=" + first.Local.Month.ToString(CultureInfo.InvariantCulture) + ";BYDAY=" + ByDay(first);
                    if (last.Local.Year < LastYear) rrule += ";UNTIL=" + CalendarValues.FormatUtc(new DateTimeOffset(last.Utc, TimeSpan.Zero));
                }

                observances.Add((first.Local, Observance(first.Daylight, first.Local, first.From, first.To, rrule)));
                i = j;
            }
        }

        foreach (var (_, observance) in observances.OrderBy(static o => o.First)) component.Components.Add(observance);
        return component;
    }

    private static CalendarComponent Observance(bool daylight, DateTime local, TimeSpan from, TimeSpan to, string? rrule) {
        var observance = new CalendarComponent(daylight ? "DAYLIGHT" : "STANDARD");
        observance.Properties.Add(new CalendarProperty("DTSTART", CalendarValues.FormatLocal(local)));
        if (rrule is not null) observance.Properties.Add(new CalendarProperty("RRULE", rrule));
        observance.Properties.Add(new CalendarProperty("TZOFFSETFROM", CalendarValues.FormatUtcOffset(from)));
        observance.Properties.Add(new CalendarProperty("TZOFFSETTO", CalendarValues.FormatUtcOffset(to)));
        return observance;
    }

    // The BYDAY value of the yearly rule an onset follows: its position in the month (-1 for the last) and weekday.
    private static string ByDay(Onset onset) {
        var local = onset.Local;
        var daysInMonth = DateTime.DaysInMonth(local.Year, local.Month);
        var position = daysInMonth - local.Day < 7 ? -1 : (local.Day - 1) / 7 + 1;
        return position.ToString(CultureInfo.InvariantCulture) + CalendarRecurrenceRule.DayCode(local.DayOfWeek);
    }

    // Two onsets of consecutive years belong to one observance when they share the rule, the time and the offsets.
    private static string RuleKey(Onset onset) =>
        ByDay(onset) + "|" + onset.Local.Month.ToString(CultureInfo.InvariantCulture)
        + "|" + onset.Local.TimeOfDay.Ticks.ToString(CultureInfo.InvariantCulture)
        + "|" + onset.From.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + onset.To.Ticks.ToString(CultureInfo.InvariantCulture);

    private static List<Onset> FindOnsets(TimeZoneInfo zone) {
        var onsets = new List<Onset>();
        var step = TimeSpan.FromDays(2);
        var current = new DateTime(FirstYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(LastYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var offset = zone.GetUtcOffset(current);
        while (current < end) {
            var next = current + step;
            var nextOffset = zone.GetUtcOffset(next);
            if (nextOffset != offset) {
                // Binary search for the first second with the new offset.
                long low = current.Ticks, high = next.Ticks;
                while (high - low > TimeSpan.TicksPerSecond) {
                    var mid = low + (high - low) / 2;
                    if (zone.GetUtcOffset(new DateTime(mid, DateTimeKind.Utc)) == offset) low = mid;
                    else high = mid;
                }

                var instant = new DateTime(high - high % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                var to = zone.GetUtcOffset(instant);
                if (to == offset) instant = instant.AddSeconds(1);
                to = zone.GetUtcOffset(instant);
                onsets.Add(new Onset(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified), offset, to, zone.IsDaylightSavingTime(instant)));
                offset = to;
                current = instant;
                continue;
            }

            current = next;
        }

        return onsets;
    }
}
