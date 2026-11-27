using System.Globalization;
using Elarion.Calendars.Reading;
using Elarion.ContentLines;

namespace Elarion.Calendars;

/// <summary>
/// One property of the component tree (spec 0004 FR-041, FR-042): its name, its parameters in order, its value as
/// written (still escaped) and the line it was read from, with typed accessors and factories that escape correctly.
/// </summary>
/// <example>
/// <code>
/// var location = CalendarProperty.Text("LOCATION", "Room 1, ground floor");   // written LOCATION:Room 1\, ground floor
/// string? text = location.GetText();                                         // "Room 1, ground floor"
/// </code>
/// </example>
public sealed class CalendarProperty {
    /// <summary>Creates a property from its value as written.</summary>
    /// <param name="name">The property name; it is kept in upper case.</param>
    /// <param name="rawValue">The value as written, escaped as its type requires.</param>
    /// <param name="parameters">The parameters.</param>
    public CalendarProperty(string name, string rawValue, params IEnumerable<CalendarParameter> parameters) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rawValue);
        ArgumentNullException.ThrowIfNull(parameters);
        Name = name.ToUpperInvariant();
        RawValue = rawValue;
        Parameters = [.. parameters];
    }

    /// <summary>The property name in upper case.</summary>
    public string Name { get; }

    /// <summary>The parameters, in order.</summary>
    public IList<CalendarParameter> Parameters { get; }

    /// <summary>The value as written, still escaped.</summary>
    public string RawValue { get; set; }

    /// <summary>The line the property was read from.</summary>
    public int? LineNumber { get; set; }

    /// <summary>The first value of the parameter named <paramref name="name"/>, ignoring case, or <see langword="null"/>.</summary>
    public string? GetParameter(string name) {
        foreach (var parameter in Parameters)
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) return parameter.Value;

        return null;
    }

    /// <summary>The value as one text, unescaped.</summary>
    public string GetText() => ContentText.Unescape(RawValue);

    /// <summary>The value as a list of texts split at unescaped commas, each unescaped.</summary>
    public IReadOnlyList<string> GetTexts() => ContentText.SplitUnescape(RawValue, ',');

    /// <summary>The value as an integer.</summary>
    public bool TryGetInteger(out int value) => CalendarValues.TryParseInteger(RawValue, out value);

    /// <summary>The value as a duration.</summary>
    public bool TryGetDuration(out TimeSpan value) => CalendarValues.TryParseDuration(RawValue, out value);

    /// <summary>The value as a recurrence rule.</summary>
    public bool TryGetRecurrenceRule(out CalendarRecurrenceRule? value) =>
        CalendarRecurrenceRule.TryParse(RawValue, parsed => CalendarItemMapper.Compose(parsed, null, null), out value);

    /// <summary>
    /// The value as a date or date-time, its <c>TZID</c> resolved by <paramref name="resolveTimeZone"/>, or through the
    /// system's zones when none is given (<see cref="CalendarTimeZones.TryResolve"/>). A value whose zone does not
    /// resolve does not read.
    /// </summary>
    public bool TryGetDateTime(out CalendarDateTime value, Func<string, TimeZoneInfo?>? resolveTimeZone = null) {
        value = default;
        if (!CalendarValues.TryParseDateTime(RawValue, out var parsed)) return false;
        var tzid = GetParameter("TZID");
        TimeZoneInfo? zone = null;
        if (!parsed.IsDate && !parsed.IsUtc && tzid is not null) {
            zone = resolveTimeZone is not null ? resolveTimeZone(tzid) : CalendarTimeZones.TryResolve(tzid, out var found) ? found : null;
            if (zone is null) return false;
        }

        value = CalendarItemMapper.Compose(parsed, zone, tzid);
        return true;
    }

    /// <summary>A copy of the property and its parameters.</summary>
    public CalendarProperty Clone() => new(Name, RawValue, Parameters.Select(static p => p.Clone())) { LineNumber = LineNumber };

    /// <summary>A text property, escaped.</summary>
    public static CalendarProperty Text(string name, string value, params IEnumerable<CalendarParameter> parameters) =>
        new(name, ContentText.Escape(value), parameters);

    /// <summary>A text-list property, each value escaped and joined by commas.</summary>
    public static CalendarProperty Texts(string name, IEnumerable<string> values, params IEnumerable<CalendarParameter> parameters) =>
        new(name, string.Join(',', values.Select(ContentText.Escape)), parameters);

    /// <summary>An integer property.</summary>
    public static CalendarProperty Integer(string name, int value) => new(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A duration property.</summary>
    public static CalendarProperty Duration(string name, TimeSpan value) => new(name, CalendarValues.FormatDuration(value));

    /// <summary>A UTC date-time property, such as <c>DTSTAMP</c>.</summary>
    public static CalendarProperty Timestamp(string name, DateTimeOffset value) => new(name, CalendarValues.FormatUtc(value));

    /// <summary>A recurrence-rule property.</summary>
    public static CalendarProperty RecurrenceRule(CalendarRecurrenceRule rule) => new("RRULE", rule.ToString());

    /// <summary>
    /// A date or date-time property: a date with <c>VALUE=DATE</c>, a zoned time with its <c>TZID</c> (the IANA id
    /// where the system can name one, spec 0004 FR-050), a UTC time with <c>Z</c>.
    /// </summary>
    public static CalendarProperty DateTime(string name, CalendarDateTime value) {
        var property = new CalendarProperty(name, FormatDateTime(value));
        AddDateTimeParameters(property.Parameters, value);
        return property;
    }

    internal static string FormatDateTime(CalendarDateTime value) => value.Kind switch {
        CalendarDateTimeKind.Date => CalendarValues.FormatDate(value.Date),
        CalendarDateTimeKind.Utc => CalendarValues.FormatUtc(new DateTimeOffset(value.Value, TimeSpan.Zero)),
        _ => CalendarValues.FormatLocal(value.Value),
    };

    internal static void AddDateTimeParameters(IList<CalendarParameter> parameters, CalendarDateTime value) {
        if (value.Kind == CalendarDateTimeKind.Date) parameters.Add(new CalendarParameter("VALUE", "DATE"));
        else if (value.Kind == CalendarDateTimeKind.Zoned) parameters.Add(new CalendarParameter("TZID", CalendarTimeZones.WrittenId(value)));
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name}:{RawValue}";
}
