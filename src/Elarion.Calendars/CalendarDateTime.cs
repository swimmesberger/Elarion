using System.Globalization;
using Elarion.Calendars.TimeZones;

namespace Elarion.Calendars;

/// <summary>What a <see cref="CalendarDateTime"/> is (spec 0004 FR-029).</summary>
public enum CalendarDateTimeKind {
    /// <summary>A date without a time, as all-day events use.</summary>
    Date,

    /// <summary>A local time without a zone: the same wall-clock time wherever it is read.</summary>
    Floating,

    /// <summary>A time in UTC.</summary>
    Utc,

    /// <summary>A local time in a time zone.</summary>
    Zoned,
}

/// <summary>
/// An iCalendar date or date-time (spec 0004 FR-029): a date, a floating date-time, a UTC date-time, or a date-time in
/// a zone, with the zone's <see cref="TimeZoneInfo"/> and the <c>TZID</c> as the file wrote it.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is the wall-clock time (midnight for a date, UTC for <see cref="CalendarDateTimeKind.Utc"/>).
/// Equality compares the kind, the value and the zone's id, not the zone's rules.
/// </remarks>
public readonly struct CalendarDateTime : IEquatable<CalendarDateTime> {
    private CalendarDateTime(CalendarDateTimeKind kind, DateTime value, TimeZoneInfo? timeZone, string? timeZoneId) {
        Kind = kind;
        Value = value;
        TimeZone = timeZone;
        TimeZoneId = timeZoneId;
    }

    /// <summary>What the value is.</summary>
    public CalendarDateTimeKind Kind { get; }

    /// <summary>The date (of the wall-clock time for a date-time).</summary>
    public DateOnly Date => DateOnly.FromDateTime(Value);

    /// <summary>
    /// The wall-clock time: midnight for a date, the local time for a floating or zoned value
    /// (<see cref="DateTimeKind.Unspecified"/>), and the UTC time for a UTC value (<see cref="DateTimeKind.Utc"/>).
    /// </summary>
    public DateTime Value { get; }

    /// <summary>The zone of a zoned value.</summary>
    public TimeZoneInfo? TimeZone { get; }

    /// <summary>The <c>TZID</c> as the file wrote it, or the zone's id for a value built in code.</summary>
    public string? TimeZoneId { get; }

    /// <summary>Whether this is a date without a time.</summary>
    public bool IsDate => Kind == CalendarDateTimeKind.Date;

    /// <summary>A date.</summary>
    public static CalendarDateTime FromDate(DateOnly date) =>
        new(CalendarDateTimeKind.Date, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), null, null);

    /// <summary>A floating date-time; the kind of <paramref name="value"/> is ignored.</summary>
    public static CalendarDateTime Floating(DateTime value) =>
        new(CalendarDateTimeKind.Floating, DateTime.SpecifyKind(TruncateToSeconds(value), DateTimeKind.Unspecified), null, null);

    /// <summary>A UTC date-time.</summary>
    public static CalendarDateTime Utc(DateTimeOffset value) =>
        new(CalendarDateTimeKind.Utc, DateTime.SpecifyKind(TruncateToSeconds(value.UtcDateTime), DateTimeKind.Utc), null, null);

    /// <summary>A date-time in <paramref name="timeZone"/>; the kind of <paramref name="value"/> is ignored.</summary>
    /// <param name="value">The wall-clock time in the zone.</param>
    /// <param name="timeZone">The zone.</param>
    /// <param name="timeZoneId">The <c>TZID</c> to keep, or <see langword="null"/> for the zone's id.</param>
    public static CalendarDateTime Zoned(DateTime value, TimeZoneInfo timeZone, string? timeZoneId = null) {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (timeZone == TimeZoneInfo.Utc) return Utc(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        return new CalendarDateTime(CalendarDateTimeKind.Zoned, DateTime.SpecifyKind(TruncateToSeconds(value), DateTimeKind.Unspecified),
            timeZone, timeZoneId ?? timeZone.Id);
    }

    /// <summary>
    /// The instant in UTC. A zoned time converts through its zone (a time in a daylight-saving gap moves forward by the
    /// gap, an ambiguous time takes its first offset, spec 0004 FR-032); a floating time or a date converts in
    /// <paramref name="floatingZone"/> and is <see langword="null"/> without one (FR-028).
    /// </summary>
    public DateTimeOffset? ToUtc(TimeZoneInfo? floatingZone = null) => Kind switch {
        CalendarDateTimeKind.Utc => new DateTimeOffset(Value, TimeSpan.Zero),
        CalendarDateTimeKind.Zoned => ZoneMath.ToUtc(Value, TimeZone!),
        _ when floatingZone is not null => ZoneMath.ToUtc(Value, floatingZone),
        _ => null,
    };

    /// <summary>This value moved by <paramref name="offset"/> in wall-clock time, keeping its kind and zone.</summary>
    public CalendarDateTime AddWallClock(TimeSpan offset) => new(Kind, Value + offset, TimeZone, TimeZoneId);

    /// <summary>The same kind and zone with another wall-clock time.</summary>
    public CalendarDateTime WithValue(DateTime value) =>
        new(Kind, DateTime.SpecifyKind(value, Kind == CalendarDateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Unspecified), TimeZone, TimeZoneId);

    /// <inheritdoc/>
    public bool Equals(CalendarDateTime other) =>
        Kind == other.Kind && Value == other.Value && string.Equals(TimeZone?.Id, other.TimeZone?.Id, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CalendarDateTime other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Value, TimeZone?.Id);

    /// <summary>Compares two values.</summary>
    public static bool operator ==(CalendarDateTime left, CalendarDateTime right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    public static bool operator !=(CalendarDateTime left, CalendarDateTime right) => !left.Equals(right);

    /// <summary>The value in iCalendar notation, with the zone's id for a zoned value.</summary>
    public override string ToString() => Kind switch {
        CalendarDateTimeKind.Date => Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        CalendarDateTimeKind.Utc => Value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        CalendarDateTimeKind.Zoned => TimeZoneId + ":" + Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
        _ => Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
    };

    private static DateTime TruncateToSeconds(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}
