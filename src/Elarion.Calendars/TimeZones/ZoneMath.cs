namespace Elarion.Calendars.TimeZones;

/// <summary>Converts wall-clock times to UTC with the rules of spec 0004 FR-032.</summary>
internal static class ZoneMath {
    /// <summary>
    /// The UTC instant of <paramref name="local"/> in <paramref name="zone"/>: a time in a gap uses the offset before
    /// the gap, which moves it forward by the gap's length; an ambiguous time takes its first (larger) offset.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone == TimeZoneInfo.Utc) return new DateTimeOffset(local, TimeSpan.Zero);
        TimeSpan offset;
        if (zone.IsInvalidTime(local)) {
            offset = zone.GetUtcOffset(SafeAdd(local, TimeSpan.FromDays(-1)));
        }
        else if (zone.IsAmbiguousTime(local)) {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            offset = offsets.Max();
        }
        else {
            offset = zone.GetUtcOffset(local);
        }

        var ticks = local.Ticks - offset.Ticks;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return new DateTimeOffset(local, TimeSpan.Zero);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>The wall-clock time of <paramref name="utc"/> in <paramref name="zone"/>.</summary>
    public static DateTime ToLocal(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(utc, zone).DateTime, DateTimeKind.Unspecified);

    public static DateTime SafeAdd(DateTime value, TimeSpan delta) {
        var ticks = value.Ticks + delta.Ticks;
        if (ticks < DateTime.MinValue.Ticks) return DateTime.MinValue;
        if (ticks > DateTime.MaxValue.Ticks) return DateTime.MaxValue;
        return new DateTime(ticks, value.Kind);
    }
}
