using System.Globalization;
using Elarion.Calendars;
using Elarion.WebDav;

namespace Elarion.CalDav;

/// <summary>
/// Evaluates a CalDAV <c>calendar-query</c> filter (RFC 4791 §9.7) against one calendar object (spec 0006 FR-019,
/// FR-022): <c>comp-filter</c>, <c>prop-filter</c>, <c>param-filter</c>, <c>text-match</c> with the collations
/// <c>i;ascii-casemap</c> (the default), <c>i;unicode-casemap</c> and <c>i;octet</c> and <c>negate-condition</c>,
/// <c>is-not-defined</c>, and <c>time-range</c> by the table of RFC 4791 §9.9 for every component kind, over the
/// occurrences of recurring components, with floating times placed in the calendar's time zone.
/// </summary>
/// <example>
/// <code>
/// var calendar = await reader.ReadAsync(stream, cancellationToken: ct);
/// if (CalDavFilterEvaluator.Matches(calendar, filterElement, vienna)) { /* answer it */ }
/// </code>
/// </example>
public static class CalDavFilterEvaluator {
    private static readonly DateTimeOffset Earliest = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The collations <c>text-match</c> supports.</summary>
    public static IReadOnlyList<string> Collations { get; } = ["i;ascii-casemap", "i;unicode-casemap", "i;octet"];

    /// <summary>Whether <paramref name="calendar"/> matches <paramref name="filter"/>, a <c>C:filter</c> element.</summary>
    /// <param name="calendar">The calendar object, read by <see cref="CalendarReader"/>.</param>
    /// <param name="filter">The <c>C:filter</c> element; validate it first with <see cref="Validate"/>.</param>
    /// <param name="floatingTimeZone">The zone floating times and dates are placed in; <see langword="null"/> for UTC.</param>
    /// <param name="caps">The expansion caps; <see langword="null"/> for the defaults.</param>
    public static bool Matches(CalendarReadResult calendar, DavXmlElement filter, TimeZoneInfo? floatingTimeZone = null, CalendarExpandOptions? caps = null) {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(filter);
        if (calendar.Root is null) return false;
        var top = filter.Child(DavNames.C.CompFilter);
        if (top is null) return false;
        var evaluation = new Evaluation(calendar, floatingTimeZone ?? TimeZoneInfo.Utc, caps ?? new CalendarExpandOptions());
        return evaluation.MatchCompFilter(null, top);
    }

    /// <summary>
    /// The problem of <paramref name="filter"/>, or <see langword="null"/> when it is valid: one <c>comp-filter</c> named
    /// <c>VCALENDAR</c> at the top, names on every filter, parseable <c>time-range</c>s with a start or an end, known
    /// collations.
    /// </summary>
    public static string? Validate(DavXmlElement? filter) {
        if (filter is null || !filter.Is(DavNames.C.Filter)) return "The calendar-query needs a CALDAV:filter element.";
        var tops = filter.ChildrenNamed(DavNames.C.CompFilter).ToList();
        if (tops.Count != 1 || !string.Equals(tops[0].Attribute("name"), "VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return "The filter must hold exactly one comp-filter named VCALENDAR.";
        return ValidateElement(tops[0]);

        static string? ValidateElement(DavXmlElement element) {
            if ((element.Is(DavNames.C.CompFilter) || element.Is(DavNames.C.PropFilter) || element.Is(DavNames.C.ParamFilter))
                && string.IsNullOrWhiteSpace(element.Attribute("name")))
                return $"A {element.Name.LocalName} needs a name.";
            if (element.Is(DavNames.C.TimeRange) && !TryParseRange(element, out _, out _))
                return "A time-range needs a start or an end in UTC, such as 20260101T000000Z.";
            if (element.Is(DavNames.C.TextMatch) && element.Attribute("collation") is { } collation && !Collations.Contains(collation))
                return $"The collation '{collation}' is not supported.";
            foreach (var child in element.Children)
                if (ValidateElement(child) is { } error)
                    return error;
            return null;
        }
    }

    /// <summary>Reads a <c>time-range</c>'s <c>start</c> and <c>end</c> (UTC, <c>yyyyMMddTHHmmssZ</c>); a missing one is open.</summary>
    public static bool TryParseRange(DavXmlElement timeRange, out DateTimeOffset start, out DateTimeOffset end) {
        ArgumentNullException.ThrowIfNull(timeRange);
        start = Earliest;
        end = Latest;
        var startText = timeRange.Attribute("start");
        var endText = timeRange.Attribute("end");
        if (startText is null && endText is null) return false;
        if (startText is not null && !TryParseUtc(startText, out start)) return false;
        if (endText is not null && !TryParseUtc(endText, out end)) return false;
        return end > start;
    }

    internal static bool TryParseUtc(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text.Trim(), ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private sealed class Evaluation {
        private readonly CalendarReadResult _calendar;
        private readonly TimeZoneInfo _zone;
        private readonly CalendarExpandOptions _caps;
        private readonly Dictionary<CalendarComponent, CalendarItem?> _items = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CalendarComponent, CalendarComponent> _parents = new(ReferenceEqualityComparer.Instance);
        private readonly Func<string, TimeZoneInfo?> _resolve;

        public Evaluation(CalendarReadResult calendar, TimeZoneInfo zone, CalendarExpandOptions caps) {
            _calendar = calendar;
            _zone = zone;
            _caps = caps with { FloatingTimeZone = zone };
            foreach (var entry in calendar.Events) _items[entry.Component] = entry.Value;
            foreach (var entry in calendar.Todos) _items[entry.Component] = entry.Value;
            Index(calendar.Root!);
            var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var vtimezone in calendar.Root!.Components.Where(static c => c.Name == "VTIMEZONE"))
                if (vtimezone.FindProperty("TZID")?.GetText() is { } id && CalendarTimeZones.TryFromComponent(vtimezone, out var tz, out _))
                    zones[id] = tz;
            _resolve = id => zones.TryGetValue(id, out var found) ? found : CalendarTimeZones.TryResolve(id, out var known) ? known : null;
        }

        private void Index(CalendarComponent component) {
            foreach (var child in component.Components) {
                _parents[child] = component;
                Index(child);
            }
        }

        public bool MatchCompFilter(CalendarComponent? parent, DavXmlElement filter) {
            var name = filter.Attribute("name") ?? "";
            IEnumerable<CalendarComponent> candidates = parent is null
                ? string.Equals(_calendar.Root!.Name, name, StringComparison.OrdinalIgnoreCase) ? [_calendar.Root] : []
                : parent.Components.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (filter.Child(DavNames.C.IsNotDefined) is not null) return !candidates.Any();
            return candidates.Any(component => MatchComponent(component, filter));
        }

        private bool MatchComponent(CalendarComponent component, DavXmlElement filter) {
            foreach (var child in filter.Children) {
                var ok = child.Name switch {
                    _ when child.Is(DavNames.C.TimeRange) => MatchTimeRange(component, child),
                    _ when child.Is(DavNames.C.PropFilter) => MatchPropFilter(component, child),
                    _ when child.Is(DavNames.C.CompFilter) => MatchCompFilter(component, child),
                    _ => true,
                };
                if (!ok) return false;
            }

            return true;
        }

        private bool MatchPropFilter(CalendarComponent component, DavXmlElement filter) {
            var name = filter.Attribute("name") ?? "";
            var properties = component.FindProperties(name.ToUpperInvariant()).ToList();
            if (filter.Child(DavNames.C.IsNotDefined) is not null) return properties.Count == 0;
            return properties.Any(property => MatchProperty(property, filter));
        }

        private bool MatchProperty(CalendarProperty property, DavXmlElement filter) {
            foreach (var child in filter.Children) {
                bool ok;
                if (child.Is(DavNames.C.TimeRange)) {
                    TryParseRange(child, out var start, out var end);
                    ok = property.TryGetDateTime(out var value, _resolve) && value.ToUtc(_zone) is { } instant && start <= instant && end > instant;
                }
                else if (child.Is(DavNames.C.TextMatch)) {
                    ok = TextMatches(property.GetText(), child);
                }
                else if (child.Is(DavNames.C.ParamFilter)) {
                    ok = MatchParamFilter(property, child);
                }
                else {
                    ok = true;
                }

                if (!ok) return false;
            }

            return true;
        }

        private static bool MatchParamFilter(CalendarProperty property, DavXmlElement filter) {
            var name = filter.Attribute("name") ?? "";
            var parameters = property.Parameters.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filter.Child(DavNames.C.IsNotDefined) is not null) return parameters.Count == 0;
            if (parameters.Count == 0) return false;
            var textMatch = filter.Child(DavNames.C.TextMatch);
            return textMatch is null || parameters.SelectMany(static p => p.Values).Any(value => TextMatches(value, textMatch));
        }

        private static bool TextMatches(string value, DavXmlElement textMatch) {
            var needle = textMatch.Text;
            var comparison = textMatch.Attribute("collation") switch {
                "i;octet" => StringComparison.Ordinal,
                "i;unicode-casemap" => StringComparison.OrdinalIgnoreCase,
                _ => StringComparison.Ordinal,
            };
            bool found;
            if (textMatch.Attribute("collation") is null or "i;ascii-casemap") found = AsciiFold(value).Contains(AsciiFold(needle), StringComparison.Ordinal);
            else found = value.Contains(needle, comparison);
            var negate = string.Equals(textMatch.Attribute("negate-condition"), "yes", StringComparison.OrdinalIgnoreCase);
            return found != negate;

            static string AsciiFold(string text) => string.Create(text.Length, text, static (span, source) => {
                for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
            });
        }

        private bool MatchTimeRange(CalendarComponent component, DavXmlElement timeRange) {
            TryParseRange(timeRange, out var start, out var end);
            switch (component.Name) {
                case "VEVENT":
                case "VTODO": {
                    if (!_items.TryGetValue(component, out var item) || item is null) return false;
                    if (item is CalendarTodo { Start: null } todo) return TodoWithoutStart(todo, start, end);
                    return OccurrencesOf(item, start, end).Any(o => item is CalendarTodo ? TodoMatches(o, start, end) : EventMatches(o, start, end));
                }
                case "VJOURNAL": {
                    if (component.FindProperty("DTSTART") is not { } dtStart || !dtStart.TryGetDateTime(out var value, _resolve)) return false;
                    if (value.ToUtc(_zone) is not { } s) return false;
                    return value.IsDate ? start < s.AddDays(1) && end > s : start <= s && end > s;
                }
                case "VFREEBUSY":
                    return FreeBusyMatches(component, start, end);
                case "VALARM":
                    return AlarmMatches(component, start, end);
                default:
                    return false;
            }
        }

        // The occurrences of one component (a master's own instances, or an override's), around the window: widened by a
        // minute, so a zero-length occurrence at the window's start is seen and the exact rule decides.
        private IEnumerable<CalendarOccurrence> OccurrencesOf(CalendarItem item, DateTimeOffset start, DateTimeOffset end) {
            var from = start == Earliest ? start : start.AddMinutes(-1);
            var to = end == Latest ? end : end.AddMinutes(1);
            var series = _calendar.Items.Where(other => other.Uid == item.Uid).ToList();
            if (series.Count == 0) series = [item];
            return CalendarRecurrence.Expand(series, from, to, _caps).Occurrences.Where(o => ReferenceEquals(o.Item, item));
        }

        // RFC 4791 §9.9, VEVENT.
        private static bool EventMatches(CalendarOccurrence occurrence, DateTimeOffset start, DateTimeOffset end) {
            if (occurrence.StartUtc is not { } s) return false;
            var e = occurrence.EndUtc ?? s;
            return occurrence.EndKind switch {
                CalendarEndKind.Stated or CalendarEndKind.DefaultDay => start < e && end > s,
                CalendarEndKind.Duration when e > s => start < e && end > s,
                _ => start <= s && end > s,
            };
        }

        // RFC 4791 §9.9, VTODO with DTSTART.
        private static bool TodoMatches(CalendarOccurrence occurrence, DateTimeOffset start, DateTimeOffset end) {
            if (occurrence.StartUtc is not { } s) return false;
            var e = occurrence.EndUtc ?? s;
            return occurrence.EndKind switch {
                CalendarEndKind.Duration => start <= e && (end > s || end >= e),
                CalendarEndKind.Stated => (start < e || start <= s) && (end > s || end >= e),
                _ => start <= s && end > s,
            };
        }

        // RFC 4791 §9.9, VTODO without DTSTART.
        private bool TodoWithoutStart(CalendarTodo todo, DateTimeOffset start, DateTimeOffset end) {
            if (todo.Due is { } dueValue && dueValue.ToUtc(_zone) is { } due) return start < due && end >= due;
            var completed = todo.Completed;
            var created = todo.Created;
            if (completed is { } c && created is { } r) return (start <= r || start <= c) && (end >= r || end >= c);
            if (completed is { } done) return start <= done && end >= done;
            if (created is { } made) return end > made;
            return true;
        }

        private bool FreeBusyMatches(CalendarComponent component, DateTimeOffset start, DateTimeOffset end) {
            var periods = component.FindProperties("FREEBUSY").SelectMany(static p => p.GetTexts()).ToList();
            if (periods.Count > 0) {
                foreach (var period in periods) {
                    var slash = period.IndexOf('/');
                    if (slash < 0 || !TryParseUtc(period[..slash], out var ps)) continue;
                    var tail = period[(slash + 1)..];
                    DateTimeOffset pe;
                    if (TryParseUtc(tail, out var explicitEnd)) pe = explicitEnd;
                    else if (new CalendarProperty("DURATION", tail).TryGetDuration(out var duration)) pe = ps + duration;
                    else continue;
                    if (start < pe && end > ps) return true;
                }

                return false;
            }

            if (component.FindProperty("DTSTART") is { } dtStart && component.FindProperty("DTEND") is { } dtEnd
                && dtStart.TryGetDateTime(out var s, _resolve) && dtEnd.TryGetDateTime(out var e, _resolve)
                && s.ToUtc(_zone) is { } from && e.ToUtc(_zone) is { } to)
                return start <= to && end > from;
            return false;
        }

        // RFC 4791 §9.9, VALARM: any trigger of any occurrence of the alarm's parent within the range.
        private bool AlarmMatches(CalendarComponent component, DateTimeOffset start, DateTimeOffset end) {
            if (!_parents.TryGetValue(component, out var parent) || !_items.TryGetValue(parent, out var item) || item is null) return false;
            var index = parent.Components.Where(static c => c.Name == "VALARM").ToList().IndexOf(component);
            if (index < 0 || index >= item.Alarms.Count) return false;
            var alarm = item.Alarms[index];
            var reach = TimeSpan.FromDays(1) + (alarm.Trigger is { } trigger ? trigger.Duration() : TimeSpan.Zero)
                                              + (alarm.RepeatInterval is { } interval ? interval * Math.Max(alarm.Repeat, 0) : TimeSpan.Zero);
            var from = start == Earliest ? start : start - reach;
            var to = end == Latest ? end : end + reach;
            return OccurrencesOf(item, from, to).SelectMany(alarm.GetTriggerTimes).Any(time => start <= time && end > time);
        }
    }
}
