using Elarion.Calendars.Recurrence;
using Elarion.Calendars.TimeZones;

namespace Elarion.Calendars;

/// <summary>
/// Expands items into their occurrences within a window (spec 0004 FR-030–FR-037): every rule, <c>RDATE</c>,
/// <c>EXDATE</c> and <c>EXRULE</c>, overrides matched by <c>UID</c> and <c>RECURRENCE-ID</c>, bounded by the caps of
/// <see cref="CalendarExpandOptions"/> and the caller's cancellation token.
/// </summary>
/// <remarks>
/// An override replaces the instance whose recurrence id it carries, even when it moves the instance into or out of
/// the window; an override whose recurrence id is no instance is an extra occurrence, as the major clients treat it.
/// The match is exact: an override of the right day at another time is an extra occurrence too. Of several overrides of
/// one instance the latest counts (<see cref="CalendarExpandOptions.ResolveOverrideVersions"/>); <see cref="LatestVersions"/>
/// returns the items with the same versions dropped.
/// <c>RANGE=THISANDFUTURE</c> is read as an override of its one instance. A <c>DURATION</c>'s whole days count in
/// wall-clock time, its remainder in elapsed time. Expansion is CPU-bound and synchronous: an application expanding
/// uploaded files passes a token with a time budget and runs it off the request thread when that matters.
/// </remarks>
/// <example>
/// <code>
/// var week = CalendarRecurrence.Expand(result.Items, monday, monday.AddDays(7),
///     new CalendarExpandOptions { FloatingTimeZone = vienna });
/// foreach (var occurrence in week.Occurrences) Console.WriteLine($"{occurrence.StartUtc}: {occurrence.Item.Summary}");
/// </code>
/// </example>
public static class CalendarRecurrence {
    /// <summary>The occurrences of <paramref name="items"/> that overlap <c>[<paramref name="from"/>, <paramref name="to"/>)</c>.</summary>
    /// <param name="items">The items of one series or of a whole calendar; series are grouped by <c>UID</c>.</param>
    /// <param name="from">The window's start, inclusive.</param>
    /// <param name="to">The window's end, exclusive.</param>
    /// <param name="options">The caps and the floating zone; <see langword="null"/> uses the defaults.</param>
    /// <param name="cancellationToken">Cancels the expansion; checked per series and per period of a rule.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static CalendarOccurrences Expand(IEnumerable<CalendarItem> items, DateTimeOffset from, DateTimeOffset to, CalendarExpandOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new CalendarExpandOptions();
        options.Validate();
        if (to <= from) return new CalendarOccurrences([]);

        var context = new ExpandContext(from.UtcDateTime, to.UtcDateTime, options, cancellationToken);
        var groups = new List<(CalendarItem? Master, List<CalendarItem> Overrides)>();
        var byUid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items) {
            if (item.Uid is null) {
                groups.Add(item.RecurrenceId is null ? (item, []) : (null, [item]));
                continue;
            }

            if (!byUid.TryGetValue(item.Uid, out var index)) {
                index = groups.Count;
                byUid[item.Uid] = index;
                groups.Add((null, []));
            }

            var group = groups[index];
            if (item.RecurrenceId is not null) group.Overrides.Add(item);
            else if (group.Master is null) groups[index] = (item, group.Overrides);
            else groups.Add((item, [])); // FR-016: a duplicate master expands on its own; overrides attach to the first.
        }

        foreach (var (master, overrides) in groups) {
            cancellationToken.ThrowIfCancellationRequested();
            if (master is null) {
                foreach (var single in context.Latest(overrides)) context.AddSingle(single, null);
                continue;
            }

            context.AddSeries(master, context.Latest(overrides));
        }

        return context.Result();
    }

    /// <summary>
    /// The items without the override versions that <see cref="CalendarExpandOptions.ResolveOverrideVersions"/> drops:
    /// of several overrides with one <c>UID</c> and <c>RECURRENCE-ID</c>, only the one that wins (the highest
    /// <c>SEQUENCE</c>, then the latest <c>DTSTAMP</c>, then the later in <paramref name="items"/>), in their order. The
    /// rule is the one <see cref="Expand"/> applies, so an application that stores or shows the items rather than the
    /// occurrences keeps the same version the expansion keeps.
    /// </summary>
    /// <param name="items">The items of one series or of a whole calendar.</param>
    /// <param name="floatingTimeZone">
    /// The zone a floating recurrence id is placed in to compare it with a zoned one, as
    /// <see cref="CalendarExpandOptions.FloatingTimeZone"/>; <see langword="null"/> compares wall-clock times.
    /// </param>
    /// <example>
    /// <code>
    /// foreach (var item in CalendarRecurrence.LatestVersions(result.Items)) await store.SaveAsync(item, ct);
    /// </code>
    /// </example>
    public static IReadOnlyList<CalendarItem> LatestVersions(IEnumerable<CalendarItem> items, TimeZoneInfo? floatingTimeZone = null) {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IReadOnlyList<CalendarItem> ?? items.ToList();
        var overridesByUid = new Dictionary<string, List<CalendarItem>>(StringComparer.Ordinal);
        foreach (var item in list) {
            if (item.Uid is null || item.RecurrenceId is null) continue;
            if (!overridesByUid.TryGetValue(item.Uid, out var overrides)) overridesByUid[item.Uid] = overrides = [];
            overrides.Add(item);
        }

        var dropped = new HashSet<CalendarItem>(ReferenceEqualityComparer.Instance);
        foreach (var overrides in overridesByUid.Values) {
            if (overrides.Count < 2) continue;
            var kept = new HashSet<CalendarItem>(LatestOf(overrides, floatingTimeZone), ReferenceEqualityComparer.Instance);
            foreach (var item in overrides)
                if (!kept.Contains(item)) dropped.Add(item);
        }

        return dropped.Count == 0 ? list : list.Where(item => !dropped.Contains(item)).ToList();
    }

    // The latest of each recurrence id (the highest SEQUENCE, then the latest DTSTAMP, then the later one), in their order.
    private static List<CalendarItem> LatestOf(List<CalendarItem> overrides, TimeZoneInfo? floatingTimeZone) {
        if (overrides.Count < 2) return overrides;
        var latest = new Dictionary<(bool, long), int>();
        for (var i = 0; i < overrides.Count; i++) {
            var id = overrides[i].RecurrenceId;
            if (id is null) continue;
            var key = (id.Value.IsDate, KeyOf(id.Value, floatingTimeZone));
            if (!latest.TryGetValue(key, out var best) || !IsNewer(overrides[best], overrides[i])) latest[key] = i;
        }

        var kept = new List<CalendarItem>(latest.Count);
        for (var i = 0; i < overrides.Count; i++) {
            var id = overrides[i].RecurrenceId;
            if (id is null || latest[(id.Value.IsDate, KeyOf(id.Value, floatingTimeZone))] == i) kept.Add(overrides[i]);
        }

        return kept;

        // Whether the earlier override a is newer than the later b; on a tie the later one counts.
        static bool IsNewer(CalendarItem a, CalendarItem b) {
            var sequence = (a.Sequence ?? 0).CompareTo(b.Sequence ?? 0);
            if (sequence != 0) return sequence > 0;
            return (a.Timestamp ?? DateTimeOffset.MinValue) > (b.Timestamp ?? DateTimeOffset.MinValue);
        }
    }

    private static long KeyOf(CalendarDateTime value, TimeZoneInfo? floatingTimeZone) =>
        value.IsDate ? value.Value.Ticks : value.ToUtc(floatingTimeZone)?.UtcTicks ?? value.Value.Ticks;

    private sealed class ExpandContext(DateTime fromUtc, DateTime toUtc, CalendarExpandOptions options, CancellationToken cancellationToken) {
        private readonly List<(DateTime Sort, CalendarOccurrence Occurrence)> _occurrences = [];
        private readonly List<CalendarError> _errors = [];

        public CalendarOccurrences Result() {
            _occurrences.Sort(static (a, b) => a.Sort.CompareTo(b.Sort));
            var list = _occurrences.Select(static o => o.Occurrence).ToList();
            if (list.Count > options.MaxOccurrences) {
                list.RemoveRange(options.MaxOccurrences, list.Count - options.MaxOccurrences);
                _errors.Add(new CalendarError(CalendarErrorCodes.TooManyOccurrences,
                    $"The expansion produced more than {options.MaxOccurrences} occurrences; the first were kept.") {
                    Limit = options.MaxOccurrences,
                });
            }

            return new CalendarOccurrences(list, _errors);
        }

        // The overrides that count: with ResolveOverrideVersions, the latest of each recurrence id.
        public List<CalendarItem> Latest(List<CalendarItem> overrides) =>
            options.ResolveOverrideVersions ? LatestOf(overrides, options.FloatingTimeZone) : overrides;

        // An item outside a series: an override without its master, or an item without a UID.
        public void AddSingle(CalendarItem item, CalendarItem? master) {
            if (Skipped(item)) return;
            var start = item.Start ?? item.RecurrenceId ?? (item as CalendarTodo)?.Due;
            if (start is null) return;
            var (end, kind) = EndOf(item, start.Value, null);
            Add(item, master, item.RecurrenceId ?? start.Value, start.Value, end, kind);
        }

        public void AddSeries(CalendarItem master, List<CalendarItem> overrides) {
            if (Skipped(master)) return;
            if (master.Start is not { } start) {
                AddSingle(master, null);
                foreach (var single in overrides) AddSingle(single, master);
                return;
            }

            foreach (var rule in master.RecurrenceRules) {
                if (!RuleEnumerator.IsImpossible(rule)) continue;
                _errors.Add(LimitError(master));
                return;
            }

            var zone = start.Kind switch {
                CalendarDateTimeKind.Zoned => start.TimeZone,
                CalendarDateTimeKind.Utc => TimeZoneInfo.Utc,
                _ => null,
            };
            var wallZone = zone ?? options.FloatingTimeZone;
            var length = EstimateLength(master, start);
            var stopLocal = ZoneMath.SafeAdd(ToWall(toUtc, wallZone), TimeSpan.FromDays(2));
            var fromLocal = ToWall(fromUtc, wallZone);
            var skipLocal = ZoneMath.SafeAdd(fromLocal, -length - TimeSpan.FromDays(2));

            // Instances: DTSTART, each rule's, and RDATEs; a period RDATE carries its own end.
            var instances = new List<(CalendarDateTime Value, CalendarPeriod? Period)>();
            if (master.RecurrenceRules.Count == 0) {
                instances.Add((start, null));
            }
            else {
                foreach (var rule in master.RecurrenceRules) {
                    var enumerator = Enumerator(rule, start, zone, stopLocal, skipLocal);
                    var inWindow = 0;
                    foreach (var instance in enumerator.Enumerate()) {
                        if (instance.Local < skipLocal) continue;
                        instances.Add((start.WithValue(instance.Local), null));
                        // The stream is ordered: past the cap within the window, later instances cannot be kept.
                        if (instance.Local >= fromLocal && ++inWindow > options.MaxOccurrences) break;
                    }

                    if (enumerator.HitIterationLimit || enumerator.HitCandidateLimit) _errors.Add(LimitError(master));
                }
            }

            foreach (var date in master.RecurrenceDates) instances.Add((date, null));
            foreach (var period in master.RecurrencePeriods) instances.Add((period.Start, period));

            var excluded = new HashSet<long>();
            var excludedDates = new HashSet<int>();
            foreach (var exception in master.ExceptionDates) {
                if (exception.IsDate && !start.IsDate) excludedDates.Add(exception.Date.DayNumber);
                else excluded.Add(Key(exception));
            }

            // RFC 2445 EXRULE: the rule's instances are excluded; DTSTART, which every rule yields first, is not.
            foreach (var rule in master.ExceptionRules) {
                if (RuleEnumerator.IsImpossible(rule)) continue; // excludes nothing
                var enumerator = Enumerator(rule, start, zone, stopLocal, skipLocal);
                foreach (var instance in enumerator.Enumerate().Skip(1)) {
                    if (instance.Local < skipLocal) continue;
                    excluded.Add(Key(start.WithValue(instance.Local)));
                }

                if (enumerator.HitIterationLimit || enumerator.HitCandidateLimit) _errors.Add(LimitError(master, "EXRULE"));
            }

            var overridden = new HashSet<long>();
            var overriddenDates = new HashSet<int>();
            foreach (var item in overrides) {
                var id = item.RecurrenceId!.Value;
                if (id.IsDate && !start.IsDate) overriddenDates.Add(id.Date.DayNumber);
                else overridden.Add(Key(id));
            }

            var seen = new HashSet<long>();
            foreach (var (value, period) in instances) {
                var key = Key(value);
                if (!seen.Add(key)) continue;
                if (excluded.Contains(key) || excludedDates.Contains(value.Date.DayNumber)) continue;
                if (overridden.Contains(key) || overriddenDates.Contains(value.Date.DayNumber)) continue;
                var (end, kind) = EndOf(master, value, period);
                Add(master, null, value, value, end, kind);
            }

            foreach (var item in overrides) AddSingle(item, master);
        }

        private RuleEnumerator Enumerator(CalendarRecurrenceRule rule, CalendarDateTime start, TimeZoneInfo? zone, DateTime stopLocal, DateTime skipLocal) =>
            new(rule, start.Value, start.IsDate, zone, BeyondUntil(rule), stopLocal, skipLocal, options.MaxIterations, options.MaxCandidates,
                cancellationToken);

        private bool Skipped(CalendarItem item) =>
            options.SkipCancelled && string.Equals(item.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

        private void Add(CalendarItem item, CalendarItem? master, CalendarDateTime recurrenceId, CalendarDateTime start, CalendarDateTime end,
            CalendarEndKind kind) {
            var startUtc = start.ToUtc(options.FloatingTimeZone);
            var endUtc = end.ToUtc(options.FloatingTimeZone);
            var startCompare = startUtc?.UtcDateTime ?? start.Value;
            var endCompare = endUtc?.UtcDateTime ?? end.Value;
            if (endCompare < startCompare) endCompare = startCompare;
            var overlaps = startCompare < toUtc && (endCompare > fromUtc || (endCompare == startCompare && startCompare >= fromUtc));
            if (!overlaps) return;
            _occurrences.Add((startCompare, new CalendarOccurrence(item, master, recurrenceId, start, end, startUtc, endUtc, kind)));
        }

        private (CalendarDateTime End, CalendarEndKind Kind) EndOf(CalendarItem item, CalendarDateTime start, CalendarPeriod? period) {
            if (period?.End is { } periodEnd) return (periodEnd, CalendarEndKind.Stated);
            if (period?.Duration is { } periodDuration) return (AddDuration(start, periodDuration), CalendarEndKind.Duration);

            var (stated, duration) = item switch {
                CalendarEvent e => (e.End, e.Duration),
                CalendarTodo t => (t.Due, t.Duration),
                _ => (null, null),
            };
            if (stated is { } end && item.Start is { } itemStart) return (Shift(itemStart, end, start), CalendarEndKind.Stated);
            if (duration is { } length) return (AddDuration(start, length), CalendarEndKind.Duration);
            return start.IsDate ? (start.AddWallClock(TimeSpan.FromDays(1)), CalendarEndKind.DefaultDay) : (start, CalendarEndKind.DefaultZero);
        }

        // The stated end moved with the instance: dates by days, a time of the same zone by wall-clock difference,
        // anything else by the elapsed time between the item's start and end.
        private CalendarDateTime Shift(CalendarDateTime itemStart, CalendarDateTime itemEnd, CalendarDateTime instance) {
            if (itemStart == instance) return itemEnd;
            if (itemStart.IsDate && itemEnd.IsDate) return instance.AddWallClock(itemEnd.Value - itemStart.Value);
            var sameFrame = itemStart.Kind == itemEnd.Kind && itemStart.TimeZone?.Id == itemEnd.TimeZone?.Id;
            if (sameFrame) return instance.AddWallClock(itemEnd.Value - itemStart.Value);
            var startUtc = itemStart.ToUtc(options.FloatingTimeZone);
            var endUtc = itemEnd.ToUtc(options.FloatingTimeZone);
            var instanceUtc = instance.ToUtc(options.FloatingTimeZone);
            if (startUtc is null || endUtc is null || instanceUtc is null) return instance.AddWallClock(itemEnd.Value - itemStart.Value);
            var endInstant = instanceUtc.Value + (endUtc.Value - startUtc.Value);
            return itemEnd.Kind == CalendarDateTimeKind.Zoned
                ? CalendarDateTime.Zoned(ZoneMath.ToLocal(endInstant, itemEnd.TimeZone!), itemEnd.TimeZone!, itemEnd.TimeZoneId)
                : CalendarDateTime.Utc(endInstant);
        }

        private CalendarDateTime AddDuration(CalendarDateTime start, TimeSpan duration) {
            var days = TimeSpan.FromDays(Math.Truncate(duration.TotalDays));
            var rest = duration - days;
            var wall = start.AddWallClock(days);
            if (rest == TimeSpan.Zero || wall.Kind != CalendarDateTimeKind.Zoned) return wall.AddWallClock(rest);
            var instant = ZoneMath.ToUtc(wall.Value, wall.TimeZone!) + rest;
            return CalendarDateTime.Zoned(ZoneMath.ToLocal(instant, wall.TimeZone!), wall.TimeZone!, wall.TimeZoneId);
        }

        private static TimeSpan EstimateLength(CalendarItem item, CalendarDateTime start) {
            var length = item switch {
                CalendarEvent { End: { } end } => end.Value - start.Value,
                CalendarEvent { Duration: { } duration } => duration,
                CalendarTodo { Due: { } due } => due.Value - start.Value,
                CalendarTodo { Duration: { } duration } => duration,
                _ => TimeSpan.FromDays(1),
            };
            foreach (var period in item.RecurrencePeriods)
                if (period.Duration is { } duration && duration > length) length = duration;

            return length < TimeSpan.Zero ? TimeSpan.Zero : length > TimeSpan.FromDays(3660) ? TimeSpan.FromDays(3660) : length;
        }

        private Func<RecurrenceInstance, bool> BeyondUntil(CalendarRecurrenceRule rule) {
            if (rule.Until is not { } until) return static _ => false;
            return until.Kind switch {
                CalendarDateTimeKind.Date => instance => DateOnly.FromDateTime(instance.Local) > until.Date,
                CalendarDateTimeKind.Utc => instance => instance.Utc is { } utc ? utc.UtcDateTime > until.Value : instance.Local > until.Value,
                _ => instance => instance.Local > until.Value,
            };
        }

        private long Key(CalendarDateTime value) => KeyOf(value, options.FloatingTimeZone);

        private static DateTime ToWall(DateTime utc, TimeZoneInfo? zone) =>
            zone is null ? DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) : ZoneMath.ToLocal(new DateTimeOffset(utc, TimeSpan.Zero), zone);

        private static CalendarError LimitError(CalendarItem item, string property = "RRULE") =>
            new(CalendarErrorCodes.RecurrenceLimit, "The recurrence examined too many candidates; its expansion stopped.") {
                Uid = item.Uid,
                Component = item.ComponentName,
                Property = property,
            };
    }
}
