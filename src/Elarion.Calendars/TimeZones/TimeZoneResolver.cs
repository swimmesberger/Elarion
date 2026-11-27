namespace Elarion.Calendars.TimeZones;

/// <summary>
/// Resolves <c>TZID</c>s for one read (spec 0004 FR-026, FR-027): through the system's zones first, then through the
/// file's <c>VTIMEZONE</c> of that id, wherever in the file it stands. Results are cached per read.
/// </summary>
internal sealed class TimeZoneResolver {
    /// <summary>A resolver that knows the system's zones only.</summary>
    public static readonly TimeZoneResolver System = new([]);

    private readonly Dictionary<string, CalendarComponent> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeZoneInfo?> _cache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public TimeZoneResolver(IEnumerable<CalendarComponent> vtimezones) {
        foreach (var component in vtimezones) {
            var tzid = component.FindProperty("TZID")?.GetText().Trim();
            if (string.IsNullOrEmpty(tzid)) continue;
            _definitions.TryAdd(tzid, component);
            _definitions.TryAdd(CalendarTimeZones.Normalize(tzid), component);
        }
    }

    public bool TryResolve(string tzid, out TimeZoneInfo zone) {
        lock (_lock) {
            if (!_cache.TryGetValue(tzid, out var found)) {
                found = Resolve(tzid);
                _cache[tzid] = found;
            }

            zone = found!;
            return found is not null;
        }
    }

    private TimeZoneInfo? Resolve(string tzid) {
        if (CalendarTimeZones.TryResolve(tzid, out var system)) return system;
        var trimmed = tzid.Trim().Trim('"').Trim();
        if (!_definitions.TryGetValue(trimmed, out var definition)
            && !_definitions.TryGetValue(CalendarTimeZones.Normalize(tzid), out definition))
            return null;

        return CalendarTimeZones.TryFromComponent(definition, out var zone, out _) ? zone : null;
    }
}
