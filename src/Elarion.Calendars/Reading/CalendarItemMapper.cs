using System.Globalization;
using Elarion.Calendars.TimeZones;
using Elarion.ContentLines;

namespace Elarion.Calendars.Reading;

/// <summary>
/// Maps a <c>VEVENT</c> or <c>VTODO</c> component to its typed item and back (spec 0004 FR-014–FR-019, FR-039–FR-042,
/// FR-046). Reading is lenient: only the time-placing properties of FR-014 leave the item without a value; any other
/// property that does not read is reported and left out (FR-015).
/// </summary>
internal static class CalendarItemMapper {
    // The properties whose failure leaves the item without a typed value (FR-014).
    private static readonly HashSet<string> TimePlacing = new(StringComparer.Ordinal) {
        "DTSTART", "DTEND", "DURATION", "DUE", "RRULE", "RDATE", "EXDATE", "RECURRENCE-ID",
    };

    public static bool TryMap(CalendarComponent component, TimeZoneResolver? resolver, out CalendarItem? item, out IReadOnlyList<CalendarError> errors) {
        var list = new List<CalendarError>();
        errors = list;
        item = null;
        CalendarItem result = component.Name switch {
            "VEVENT" => new CalendarEvent(),
            "VTODO" => new CalendarTodo(),
            _ => throw new ArgumentException($"A {component.Name} component is not an event or a to-do.", nameof(component)),
        };

        resolver ??= TimeZoneResolver.System;
        var valid = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in component.Properties) {
            var error = MapProperty(result, property, resolver, seen);
            if (error is null) continue;
            list.Add(error);
            if (TimePlacing.Contains(property.Name)) valid = false;
        }

        foreach (var sub in component.Components) {
            if (sub.Name == "VALARM") result.Alarms.Add(MapAlarm(sub, list));
            else result.AdditionalComponents.Add(sub.Clone());
        }

        if (result is CalendarEvent { Start: null } && valid) {
            list.Add(new CalendarError(CalendarErrorCodes.Required, "The event has no start (DTSTART).") { Property = "DTSTART" });
            valid = false;
        }

        if (result is CalendarEvent calendarEvent) ApplyMicrosoftAllDay(calendarEvent);
        if (!valid) return false;
        item = result;
        return true;
    }

    public static CalendarDateTime Compose(ParsedDateTime parsed, TimeZoneInfo? zone, string? tzid) {
        if (parsed.IsDate) return CalendarDateTime.FromDate(parsed.Date);
        if (parsed.IsUtc) return CalendarDateTime.Utc(new DateTimeOffset(parsed.ToDateTime(), TimeSpan.Zero));
        if (zone is not null) return CalendarDateTime.Zoned(parsed.ToDateTime(), zone, tzid);
        return CalendarDateTime.Floating(parsed.ToDateTime());
    }

    // ---- reading ------------------------------------------------------------------------------------------------

    private static CalendarError? MapProperty(CalendarItem item, CalendarProperty property, TimeZoneResolver resolver, HashSet<string> seen) {
        var name = property.Name;
        var single = name is "UID" or "DTSTAMP" or "CREATED" or "LAST-MODIFIED" or "SEQUENCE" or "DTSTART" or "DTEND" or "DUE"
            or "DURATION" or "RECURRENCE-ID" or "SUMMARY" or "DESCRIPTION" or "LOCATION" or "CLASS" or "PRIORITY" or "URL" or "GEO"
            or "ORGANIZER" or "STATUS" or "COLOR" or "TRANSP" or "COMPLETED" or "PERCENT-COMPLETE";
        if (single && !seen.Add(name)) {
            // A second single-valued property is not the model's to choose between; keep it as written.
            item.AdditionalProperties.Add(property.Clone());
            return null;
        }

        var todo = item as CalendarTodo;
        var calendarEvent = item as CalendarEvent;
        switch (name) {
            case "UID": item.Uid = property.GetText(); return null;
            case "DTSTAMP": return Timestamp(property, resolver, value => item.Timestamp = value);
            case "CREATED": return Timestamp(property, resolver, value => item.Created = value);
            case "LAST-MODIFIED": return Timestamp(property, resolver, value => item.LastModified = value);
            case "SEQUENCE": return Integer(property, value => item.Sequence = value);
            case "PRIORITY": return Integer(property, value => item.Priority = value);
            case "DTSTART": return DateTimeValue(property, resolver, value => item.Start = value);
            case "RECURRENCE-ID": return DateTimeValue(property, resolver, value => item.RecurrenceId = value);
            case "DTEND" when calendarEvent is not null: return DateTimeValue(property, resolver, value => calendarEvent.End = value);
            case "DUE" when todo is not null: return DateTimeValue(property, resolver, value => todo.Due = value);
            case "DURATION":
                if (!property.TryGetDuration(out var duration)) return Invalid(property, "duration");
                if (calendarEvent is not null) calendarEvent.Duration = duration;
                else if (todo is not null) todo.Duration = duration;
                return null;
            case "COMPLETED" when todo is not null: return Timestamp(property, resolver, value => todo.Completed = value);
            case "PERCENT-COMPLETE" when todo is not null:
                return Integer(property, value => todo.PercentComplete = value, 0, 100);
            case "RRULE":
                if (!CalendarRecurrenceRule.TryParse(property.RawValue, parsed => Compose(parsed, null, null), out var rule))
                    return Invalid(property, "recurrence");
                item.RecurrenceRules.Add(rule!);
                return null;
            case "RDATE": return RecurrenceDates(item, property, resolver);
            case "EXDATE": return DateTimeList(property, resolver, item.ExceptionDates);
            case "SUMMARY": item.Summary = property.GetText(); return null;
            case "DESCRIPTION": item.Description = property.GetText(); return null;
            case "LOCATION": item.Location = property.GetText(); return null;
            case "STATUS": item.Status = property.GetText().Trim().ToUpperInvariant(); return null;
            case "COLOR": item.Color = property.GetText(); return null;
            case "CATEGORIES": Append(item.Categories, property.GetTexts()); return null;
            case "RESOURCES": Append(item.Resources, property.GetTexts()); return null;
            case "COMMENT": item.Comments.Add(property.GetText()); return null;
            case "CONTACT": item.Contacts.Add(property.GetText()); return null;
            case "RELATED-TO": item.RelatedTo.Add(property.GetText()); return null;
            case "CLASS":
                CalendarClassification? classification = property.GetText().Trim().ToUpperInvariant() switch {
                    "PUBLIC" => CalendarClassification.Public,
                    "PRIVATE" => CalendarClassification.Private,
                    "CONFIDENTIAL" => CalendarClassification.Confidential,
                    _ => null,
                };
                if (classification is null) item.AdditionalProperties.Add(property.Clone());
                item.Class = classification;
                return null;
            case "TRANSP" when calendarEvent is not null:
                CalendarTransparency? transparency = property.GetText().Trim().ToUpperInvariant() switch {
                    "OPAQUE" => CalendarTransparency.Opaque,
                    "TRANSPARENT" => CalendarTransparency.Transparent,
                    _ => null,
                };
                if (transparency is null) calendarEvent.AdditionalProperties.Add(property.Clone());
                calendarEvent.Transparency = transparency;
                return null;
            case "URL":
                if (!Uri.TryCreate(property.GetText().Trim(), UriKind.Absolute, out var url)) return Invalid(property, "uri");
                item.Url = url;
                return null;
            case "GEO":
                var parts = property.RawValue.Split(';', ',');
                if (parts.Length != 2
                    || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                    || latitude is < -90 or > 90 || longitude is < -180 or > 180)
                    return Invalid(property, "geo");
                item.Geo = (latitude, longitude);
                return null;
            case "ORGANIZER":
                item.Organizer = new CalendarOrganizer {
                    Address = property.RawValue.Trim(),
                    CommonName = property.GetParameter("CN"),
                    AdditionalParameters = Rest(property, "CN"),
                };
                return null;
            case "ATTENDEE":
                var rsvp = property.GetParameter("RSVP");
                item.Attendees.Add(new CalendarAttendee {
                    Address = property.RawValue.Trim(),
                    CommonName = property.GetParameter("CN"),
                    Role = property.GetParameter("ROLE")?.ToUpperInvariant(),
                    ParticipationStatus = property.GetParameter("PARTSTAT")?.ToUpperInvariant(),
                    Rsvp = rsvp is null ? null : rsvp.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    UserType = property.GetParameter("CUTYPE")?.ToUpperInvariant(),
                    AdditionalParameters = Rest(property, "CN", "ROLE", "PARTSTAT", "RSVP", "CUTYPE"),
                });
                return null;
            case "CONFERENCE":
                var features = property.Parameters.Where(static p => p.Name == "FEATURE").SelectMany(static p => p.Values).ToList();
                item.Conferences.Add(new CalendarConference {
                    Uri = property.RawValue.Trim(),
                    Features = features,
                    Label = property.GetParameter("LABEL"),
                });
                return null;
            default:
                item.AdditionalProperties.Add(property.Clone());
                return null;
        }
    }

    private static void Append(IList<string> target, IReadOnlyList<string> values) {
        foreach (var value in values) {
            var trimmed = value.Trim();
            if (trimmed.Length > 0) target.Add(trimmed);
        }
    }

    private static List<CalendarParameter> Rest(CalendarProperty property, params string[] mapped) =>
        [.. property.Parameters.Where(p => !mapped.Contains(p.Name)).Select(static p => p.Clone())];

    private static CalendarError? Integer(CalendarProperty property, Action<int> set, int min = int.MinValue, int max = int.MaxValue) {
        if (!property.TryGetInteger(out var value) || value < min || value > max) return Invalid(property, "integer");
        set(value);
        return null;
    }

    private static CalendarError? Timestamp(CalendarProperty property, TimeZoneResolver resolver, Action<DateTimeOffset> set) {
        if (!CalendarValues.TryParseDateTime(property.RawValue, out var parsed)) return Invalid(property, "date-time");
        // FR-017: a date where a timestamp belongs is midnight UTC; a floating timestamp reads as UTC.
        var tzid = property.GetParameter("TZID");
        if (!parsed.IsDate && !parsed.IsUtc && tzid is not null && resolver.TryResolve(tzid, out var zone)) {
            set(ZoneMath.ToUtc(parsed.ToDateTime(), zone));
            return null;
        }

        set(new DateTimeOffset(parsed.ToDateTime(), TimeSpan.Zero));
        return null;
    }

    private static CalendarError? DateTimeValue(CalendarProperty property, TimeZoneResolver resolver, Action<CalendarDateTime> set) {
        var error = ParseDateTime(property, property.RawValue, resolver, out var value);
        if (error is null) set(value);
        return error;
    }

    private static CalendarError? DateTimeList(CalendarProperty property, TimeZoneResolver resolver, IList<CalendarDateTime> target) {
        foreach (var part in property.RawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var error = ParseDateTime(property, part, resolver, out var value);
            if (error is not null) return error;
            target.Add(value);
        }

        return null;
    }

    private static CalendarError? RecurrenceDates(CalendarItem item, CalendarProperty property, TimeZoneResolver resolver) {
        if (!string.Equals(property.GetParameter("VALUE"), "PERIOD", StringComparison.OrdinalIgnoreCase))
            return DateTimeList(property, resolver, item.RecurrenceDates);

        foreach (var part in property.RawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var slash = part.IndexOf('/');
            if (slash <= 0) return Invalid(property, "period");
            var error = ParseDateTime(property, part[..slash], resolver, out var start);
            if (error is not null) return error;
            var second = part[(slash + 1)..];
            if (second.StartsWith('P') || second.StartsWith("+P", StringComparison.Ordinal) || second.StartsWith('p')) {
                if (!CalendarValues.TryParseDuration(second, out var length)) return Invalid(property, "period");
                item.RecurrencePeriods.Add(new CalendarPeriod { Start = start, Duration = length });
            }
            else {
                error = ParseDateTime(property, second, resolver, out var end);
                if (error is not null) return error;
                item.RecurrencePeriods.Add(new CalendarPeriod { Start = start, End = end });
            }
        }

        return null;
    }

    private static CalendarError? ParseDateTime(CalendarProperty property, string text, TimeZoneResolver resolver, out CalendarDateTime value) {
        value = default;
        if (!CalendarValues.TryParseDateTime(text, out var parsed)) return Invalid(property, "date-time");
        if (string.Equals(property.GetParameter("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase) && !parsed.IsDate)
            parsed = parsed with { Time = null, IsUtc = false };

        TimeZoneInfo? zone = null;
        var tzid = property.GetParameter("TZID");
        // FR-017: a TZID on a date, or on a UTC time, is ignored.
        if (!parsed.IsDate && !parsed.IsUtc && tzid is not null && !resolver.TryResolve(tzid, out zone)) {
            return new CalendarError(CalendarErrorCodes.UnknownTimeZone, $"The time zone '{tzid}' is not known to the system or defined in the file.") {
                Property = property.Name,
                LineNumber = property.LineNumber,
                Value = property.RawValue,
                TimeZoneId = tzid,
            };
        }

        value = Compose(parsed, zone, tzid);
        return null;
    }

    internal static CalendarError Invalid(CalendarProperty property, string kind) =>
        new(CalendarErrorCodes.InvalidValue, $"The value '{Shorten(property.RawValue)}' is not a valid {kind}.") {
            Property = property.Name,
            LineNumber = property.LineNumber,
            Value = property.RawValue,
            ExpectedKind = kind,
        };

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "…";

    // FR-019: older Exchange marks all-day events by a property and midnight times.
    private static void ApplyMicrosoftAllDay(CalendarEvent item) {
        if (item.Start is not { IsDate: false } start || item.End is not { IsDate: false } end) return;
        var flagged = item.AdditionalProperties.Any(static p =>
            p.Name is "X-MICROSOFT-CDO-ALLDAYEVENT" or "X-MICROSOFT-MSNCALENDAR-ALLDAYEVENT"
            && p.RawValue.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase));
        if (!flagged || start.Value.TimeOfDay != TimeSpan.Zero || end.Value.TimeOfDay != TimeSpan.Zero) return;
        item.Start = CalendarDateTime.FromDate(start.Date);
        item.End = CalendarDateTime.FromDate(end.Date);
    }

    private static CalendarAlarm MapAlarm(CalendarComponent component, List<CalendarError> errors) {
        var alarm = new CalendarAlarm();
        var seenAction = false;
        foreach (var property in component.Properties) {
            switch (property.Name) {
                case "ACTION" when !seenAction:
                    seenAction = true;
                    var text = property.GetText().Trim().ToUpperInvariant();
                    alarm.Action = text switch {
                        "AUDIO" => CalendarAlarmAction.Audio,
                        "DISPLAY" => CalendarAlarmAction.Display,
                        "EMAIL" => CalendarAlarmAction.Email,
                        _ => CalendarAlarmAction.Unknown,
                    };
                    alarm.ActionText = alarm.Action == CalendarAlarmAction.Unknown ? property.GetText().Trim() : null;
                    break;
                case "TRIGGER":
                    if (string.Equals(property.GetParameter("VALUE"), "DATE-TIME", StringComparison.OrdinalIgnoreCase)
                        || (CalendarValues.TryParseDateTime(property.RawValue, out _) && !property.RawValue.TrimStart().StartsWith('-')
                            && !property.RawValue.TrimStart().StartsWith('P'))) {
                        if (CalendarValues.TryParseDateTime(property.RawValue, out var absolute))
                            alarm.AbsoluteTrigger = new DateTimeOffset(absolute.ToDateTime(), TimeSpan.Zero);
                        else errors.Add(Invalid(property, "date-time"));
                    }
                    else if (property.TryGetDuration(out var trigger)) {
                        alarm.Trigger = trigger;
                        alarm.Related = string.Equals(property.GetParameter("RELATED"), "END", StringComparison.OrdinalIgnoreCase)
                            ? CalendarAlarmRelation.End
                            : CalendarAlarmRelation.Start;
                    }
                    else {
                        errors.Add(Invalid(property, "duration"));
                    }

                    break;
                case "UID": alarm.Uid = property.GetText(); break;
                case "REPEAT":
                    if (property.TryGetInteger(out var repeat) && repeat >= 0) alarm.Repeat = repeat;
                    else errors.Add(Invalid(property, "integer"));
                    break;
                case "DURATION":
                    if (property.TryGetDuration(out var interval)) alarm.RepeatInterval = interval;
                    else errors.Add(Invalid(property, "duration"));
                    break;
                case "DESCRIPTION": alarm.Description = property.GetText(); break;
                case "SUMMARY": alarm.Summary = property.GetText(); break;
                case "ACKNOWLEDGED":
                    if (CalendarValues.TryParseDateTime(property.RawValue, out var acknowledged))
                        alarm.Acknowledged = new DateTimeOffset(acknowledged.ToDateTime(), TimeSpan.Zero);
                    else errors.Add(Invalid(property, "date-time"));
                    break;
                default:
                    alarm.AdditionalProperties.Add(property.Clone());
                    break;
            }
        }

        foreach (var sub in component.Components) alarm.AdditionalComponents.Add(sub.Clone());
        return alarm;
    }

    // ---- writing ------------------------------------------------------------------------------------------------

    public static CalendarComponent ToComponent(CalendarItem item, DateTimeOffset? timestamp) {
        var component = new CalendarComponent(item.ComponentName);
        var p = component.Properties;
        if (item.Uid is not null) p.Add(CalendarProperty.Text("UID", item.Uid));
        if ((item.Timestamp ?? timestamp) is { } stamp) p.Add(CalendarProperty.Timestamp("DTSTAMP", stamp));
        if (item.Created is { } created) p.Add(CalendarProperty.Timestamp("CREATED", created));
        if (item.LastModified is { } modified) p.Add(CalendarProperty.Timestamp("LAST-MODIFIED", modified));
        if (item.Sequence is { } sequence) p.Add(CalendarProperty.Integer("SEQUENCE", sequence));
        if (item.Start is { } start) p.Add(CalendarProperty.DateTime("DTSTART", start));
        switch (item) {
            case CalendarEvent { End: { } end }: p.Add(CalendarProperty.DateTime("DTEND", end)); break;
            case CalendarTodo { Due: { } due }: p.Add(CalendarProperty.DateTime("DUE", due)); break;
        }

        var duration = item switch {
            CalendarEvent e => e.Duration,
            CalendarTodo t => t.Duration,
            _ => null,
        };
        if (duration is { } length) p.Add(CalendarProperty.Duration("DURATION", length));
        if (item.RecurrenceId is { } recurrenceId) p.Add(CalendarProperty.DateTime("RECURRENCE-ID", recurrenceId));
        foreach (var rule in item.RecurrenceRules) p.Add(CalendarProperty.RecurrenceRule(rule));
        foreach (var date in item.RecurrenceDates) p.Add(CalendarProperty.DateTime("RDATE", date));
        foreach (var period in item.RecurrencePeriods) {
            var property = CalendarProperty.DateTime("RDATE", period.Start);
            property.Parameters.Insert(0, new CalendarParameter("VALUE", "PERIOD"));
            property.RawValue += "/" + (period.End is { } periodEnd
                ? CalendarProperty.FormatDateTime(periodEnd)
                : CalendarValues.FormatDuration(period.Duration ?? TimeSpan.Zero));
            p.Add(property);
        }

        foreach (var date in item.ExceptionDates) p.Add(CalendarProperty.DateTime("EXDATE", date));
        if (item.Summary is not null) p.Add(CalendarProperty.Text("SUMMARY", item.Summary));
        if (item.Description is not null) p.Add(CalendarProperty.Text("DESCRIPTION", item.Description));
        if (item.Location is not null) p.Add(CalendarProperty.Text("LOCATION", item.Location));
        if (item.Geo is { } geo)
            p.Add(new CalendarProperty("GEO", geo.Latitude.ToString("R", CultureInfo.InvariantCulture) + ";"
                                              + geo.Longitude.ToString("R", CultureInfo.InvariantCulture)));
        if (item.Categories.Count > 0) p.Add(CalendarProperty.Texts("CATEGORIES", item.Categories));
        if (item.Class is { } classification) p.Add(new CalendarProperty("CLASS", classification.ToString().ToUpperInvariant()));
        if (item.Priority is { } priority) p.Add(CalendarProperty.Integer("PRIORITY", priority));
        if (item.Status is not null) p.Add(CalendarProperty.Text("STATUS", item.Status));
        if (item is CalendarEvent { Transparency: { } transparency })
            p.Add(new CalendarProperty("TRANSP", transparency.ToString().ToUpperInvariant()));
        if (item.Url is not null) p.Add(new CalendarProperty("URL", item.Url.OriginalString));
        if (item.Organizer is { } organizer) {
            var property = new CalendarProperty("ORGANIZER", organizer.Address);
            if (organizer.CommonName is not null) property.Parameters.Add(new CalendarParameter("CN", organizer.CommonName));
            foreach (var parameter in organizer.AdditionalParameters) property.Parameters.Add(parameter.Clone());
            p.Add(property);
        }

        foreach (var attendee in item.Attendees) {
            var property = new CalendarProperty("ATTENDEE", attendee.Address);
            if (attendee.CommonName is not null) property.Parameters.Add(new CalendarParameter("CN", attendee.CommonName));
            if (attendee.UserType is not null) property.Parameters.Add(new CalendarParameter("CUTYPE", attendee.UserType));
            if (attendee.Role is not null) property.Parameters.Add(new CalendarParameter("ROLE", attendee.Role));
            if (attendee.ParticipationStatus is not null) property.Parameters.Add(new CalendarParameter("PARTSTAT", attendee.ParticipationStatus));
            if (attendee.Rsvp is { } rsvp) property.Parameters.Add(new CalendarParameter("RSVP", rsvp ? "TRUE" : "FALSE"));
            foreach (var parameter in attendee.AdditionalParameters) property.Parameters.Add(parameter.Clone());
            p.Add(property);
        }

        if (item.Color is not null) p.Add(CalendarProperty.Text("COLOR", item.Color));
        foreach (var conference in item.Conferences) {
            var property = new CalendarProperty("CONFERENCE", conference.Uri, new CalendarParameter("VALUE", "URI"));
            if (conference.Features.Count > 0) property.Parameters.Add(new CalendarParameter("FEATURE", conference.Features));
            if (conference.Label is not null) property.Parameters.Add(new CalendarParameter("LABEL", conference.Label));
            p.Add(property);
        }

        if (item is CalendarTodo todo) {
            if (todo.Completed is { } completed) p.Add(CalendarProperty.Timestamp("COMPLETED", completed));
            if (todo.PercentComplete is { } percent) p.Add(CalendarProperty.Integer("PERCENT-COMPLETE", percent));
        }

        foreach (var comment in item.Comments) p.Add(CalendarProperty.Text("COMMENT", comment));
        foreach (var contact in item.Contacts) p.Add(CalendarProperty.Text("CONTACT", contact));
        if (item.Resources.Count > 0) p.Add(CalendarProperty.Texts("RESOURCES", item.Resources));
        foreach (var related in item.RelatedTo) p.Add(CalendarProperty.Text("RELATED-TO", related));
        foreach (var property in item.AdditionalProperties) p.Add(property.Clone());
        foreach (var alarm in item.Alarms) component.Components.Add(AlarmToComponent(alarm));
        foreach (var sub in item.AdditionalComponents) component.Components.Add(sub.Clone());
        return component;
    }

    private static CalendarComponent AlarmToComponent(CalendarAlarm alarm) {
        var component = new CalendarComponent("VALARM");
        var p = component.Properties;
        if (alarm.Uid is not null) p.Add(CalendarProperty.Text("UID", alarm.Uid));
        p.Add(new CalendarProperty("ACTION", alarm.Action == CalendarAlarmAction.Unknown
            ? alarm.ActionText ?? "NONE"
            : alarm.Action.ToString().ToUpperInvariant()));
        if (alarm.AbsoluteTrigger is { } absolute) {
            p.Add(new CalendarProperty("TRIGGER", CalendarValues.FormatUtc(absolute), new CalendarParameter("VALUE", "DATE-TIME")));
        }
        else if (alarm.Trigger is { } trigger) {
            var property = CalendarProperty.Duration("TRIGGER", trigger);
            if (alarm.Related == CalendarAlarmRelation.End) property.Parameters.Add(new CalendarParameter("RELATED", "END"));
            p.Add(property);
        }

        if (alarm.Repeat > 0) p.Add(CalendarProperty.Integer("REPEAT", alarm.Repeat));
        if (alarm.RepeatInterval is { } interval) p.Add(CalendarProperty.Duration("DURATION", interval));
        if (alarm.Description is not null) p.Add(CalendarProperty.Text("DESCRIPTION", alarm.Description));
        if (alarm.Summary is not null) p.Add(CalendarProperty.Text("SUMMARY", alarm.Summary));
        if (alarm.Acknowledged is { } acknowledged) p.Add(CalendarProperty.Timestamp("ACKNOWLEDGED", acknowledged));
        foreach (var property in alarm.AdditionalProperties) p.Add(property.Clone());
        foreach (var sub in alarm.AdditionalComponents) component.Components.Add(sub.Clone());
        return component;
    }

    public static IReadOnlyList<TimeZoneInfo> TimeZonesOf(CalendarItem item) {
        var zones = new List<TimeZoneInfo>();
        void Add(CalendarDateTime? value) {
            if (value is { Kind: CalendarDateTimeKind.Zoned, TimeZone: { } zone } && zones.All(z => z.Id != zone.Id)) zones.Add(zone);
        }

        Add(item.Start);
        if (item is CalendarEvent e) Add(e.End);
        if (item is CalendarTodo t) Add(t.Due);
        Add(item.RecurrenceId);
        foreach (var date in item.RecurrenceDates) Add(date);
        foreach (var period in item.RecurrencePeriods) {
            Add(period.Start);
            Add(period.End);
        }

        foreach (var date in item.ExceptionDates) Add(date);
        return zones;
    }

    public static string EscapeForError(string value) => ContentText.Escape(value);
}
