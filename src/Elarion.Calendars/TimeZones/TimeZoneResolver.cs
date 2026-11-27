namespace Elarion.Calendars.TimeZones;

/// <summary>
/// Resolves <c>TZID</c>s for one read (spec 0004 FR-026, FR-027): through the system's zones first, then through the
/// file's <c>VTIMEZONE</c> of that id, wherever in the file it stands. An id longer than the read's limit is not
/// resolved at all. Results are cached per read, never across reads.
/// </summary>
internal sealed class TimeZoneResolver {
    /// <summary>
    /// A resolver that knows the system's zones only, with the default id limit. It is shared, so it caches nothing:
    /// a cache in it would keep every id it was asked for, for the life of the process.
    /// </summary>
    public static readonly TimeZoneResolver System = new([], CalendarReadOptions.DefaultMaxTimeZoneIdLength, cache: false);

    private readonly Dictionary<string, CalendarComponent> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeZoneInfo?>? _cache;
    private readonly int _maxIdLength;
    private readonly Lock _lock = new();

    public TimeZoneResolver(IEnumerable<CalendarComponent> vtimezones, int maxIdLength, bool cache = true) {
        _maxIdLength = maxIdLength;
        _cache = cache ? new Dictionary<string, TimeZoneInfo?>(StringComparer.Ordinal) : null;
        foreach (var component in vtimezones) {
            var tzid = component.FindProperty("TZID")?.GetText().Trim();
            if (string.IsNullOrEmpty(tzid) || tzid.Length > maxIdLength) continue;
            _definitions.TryAdd(tzid, component);
            _definitions.TryAdd(CalendarTimeZones.Normalize(tzid), component);
        }
    }

    public bool TryResolve(string tzid, out TimeZoneInfo zone) {
        TimeZoneInfo? found;
        if (tzid.Length > _maxIdLength) {
            found = null;
        }
        else if (_cache is null) {
            found = Resolve(tzid);
        }
        else {
            lock (_lock) {
                if (!_cache.TryGetValue(tzid, out found)) {
                    found = Resolve(tzid);
                    _cache[tzid] = found;
                }
            }
        }

        zone = found!;
        return found is not null;
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
