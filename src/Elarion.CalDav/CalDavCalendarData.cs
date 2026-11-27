using System.Text;
using Elarion.Calendars;
using Elarion.WebDav;

namespace Elarion.CalDav;

/// <summary>
/// Renders the <c>calendar-data</c> of a report (RFC 4791 §9.6): the stored bytes as they are, or a component tree that
/// <c>expand</c>, <c>limit-recurrence-set</c>, a partial <c>comp</c>/<c>prop</c> selection and <c>CalDAV-Timezones: F</c>
/// (RFC 7809) rewrote, written again through <see cref="CalendarWriter"/>.
/// </summary>
internal static class CalDavCalendarData {
    private static readonly string[] RecurrenceProperties = ["RRULE", "RDATE", "EXDATE", "EXRULE"];

    /// <summary>Whether the request asks for the stored bytes unchanged.</summary>
    public static bool IsVerbatim(DavXmlElement? requested, bool includeTimeZones) =>
        includeTimeZones && (requested is null || requested.Children.Count == 0);

    public static async ValueTask<string> RenderAsync(ReadOnlyMemory<byte> stored, DavXmlElement? requested, bool includeTimeZones,
        CalendarReader reader, CalDavOptions options, TimeZoneInfo zone, CancellationToken cancellationToken) {
        if (IsVerbatim(requested, includeTimeZones)) return Encoding.UTF8.GetString(stored.Span);

        using var input = new MemoryStream(stored.ToArray(), false);
        var calendar = await reader.ReadAsync(input, options.CalendarRead, cancellationToken).ConfigureAwait(false);
        if (calendar.Root is null) return Encoding.UTF8.GetString(stored.Span);
        var root = calendar.Root;
        var caps = options.CalendarExpand with { FloatingTimeZone = zone };

        if (requested?.Child(DavNames.C.Expand) is { } expand && CalDavFilterEvaluator.TryParseRange(expand, out var from, out var to)) {
            root = Expand(calendar, from, to, caps);
            includeTimeZones = false; // every expanded time is in UTC
        }
        else if (requested?.Child(DavNames.C.LimitRecurrenceSet) is { } limit && CalDavFilterEvaluator.TryParseRange(limit, out var lFrom, out var lTo)) {
            LimitRecurrenceSet(calendar, lFrom, lTo, caps);
        }

        if (!includeTimeZones) RemoveTimeZones(root, requested?.Child(DavNames.C.Expand) is not null);
        if (requested?.Child(DavNames.C.Comp) is { } comp) Prune(root, comp);

        using var output = new MemoryStream();
        await CalendarWriter.WriteAsync(output, root, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    // RFC 4791 §9.6.5: every instance overlapping the range as its own component in UTC, no rules, no time zones.
    private static CalendarComponent Expand(CalendarReadResult calendar, DateTimeOffset from, DateTimeOffset to, CalendarExpandOptions caps) {
        var source = calendar.Root!;
        var root = new CalendarComponent(source.Name);
        foreach (var property in source.Properties) root.Properties.Add(property.Clone());
        foreach (var other in source.Components.Where(static c => c.Name is not ("VTIMEZONE" or "VEVENT" or "VTODO"))) root.Components.Add(other.Clone());

        var components = new Dictionary<CalendarItem, CalendarComponent>(ReferenceEqualityComparer.Instance);
        foreach (var entry in calendar.Events.Where(static e => e.HasValue)) components[entry.Value!] = entry.Component;
        foreach (var entry in calendar.Todos.Where(static e => e.HasValue)) components[entry.Value!] = entry.Component;

        foreach (var occurrence in CalendarRecurrence.Expand(calendar.Items, from, to, caps).Occurrences) {
            if (!components.TryGetValue(occurrence.Item, out var original)) continue;
            var instance = original.Clone();
            foreach (var name in RecurrenceProperties) RemoveAll(instance, name);
            Set(instance, "DTSTART", Utc(occurrence.Start, occurrence.StartUtc));
            var endName = occurrence.Item is CalendarTodo ? "DUE" : "DTEND";
            if (instance.FindProperty(endName) is not null) Set(instance, endName, Utc(occurrence.End, occurrence.EndUtc));
            var recurring = occurrence.Master is not null || occurrence.Item.IsRecurring;
            if (recurring) Set(instance, "RECURRENCE-ID", Utc(occurrence.RecurrenceId, occurrence.RecurrenceId.ToUtc(caps.FloatingTimeZone)));
            root.Components.Add(instance);
        }

        return root;

        static CalendarDateTime Utc(CalendarDateTime value, DateTimeOffset? utc) =>
            value.IsDate || utc is null ? value : CalendarDateTime.Utc(utc.Value);
    }

    // RFC 4791 §9.6.6: the master, and only the overrides whose instance overlaps the range.
    private static void LimitRecurrenceSet(CalendarReadResult calendar, DateTimeOffset from, DateTimeOffset to, CalendarExpandOptions caps) {
        var inRange = new HashSet<CalendarItem>(ReferenceEqualityComparer.Instance);
        foreach (var occurrence in CalendarRecurrence.Expand(calendar.Items, from, to, caps).Occurrences) inRange.Add(occurrence.Item);
        var root = calendar.Root!;
        foreach (var entry in calendar.Events.Cast<object>().Concat(calendar.Todos)) {
            var (component, value) = entry switch {
                CalendarEntry<CalendarEvent> e => (e.Component, (CalendarItem?)e.Value),
                CalendarEntry<CalendarTodo> t => (t.Component, t.Value),
                _ => (null!, null),
            };
            if (value is { RecurrenceId: not null } && !inRange.Contains(value)) root.Components.Remove(component);
        }
    }

    // RFC 7809: with CalDAV-Timezones: F the client resolves standard zone ids itself; a custom zone stays.
    private static void RemoveTimeZones(CalendarComponent root, bool all) {
        for (var i = root.Components.Count - 1; i >= 0; i--) {
            var component = root.Components[i];
            if (component.Name != "VTIMEZONE") continue;
            if (all || (component.FindProperty("TZID")?.GetText() is { } id && CalendarTimeZones.TryResolve(id, out var zone)
                        && CalendarTimeZones.HasIanaId(zone)))
                root.Components.RemoveAt(i);
        }
    }

    // RFC 4791 §9.6.1: keep the named properties and sub-components, recursively.
    private static void Prune(CalendarComponent component, DavXmlElement spec) {
        if (spec.Child(DavNames.C.AllProp) is null) {
            var keep = spec.ChildrenNamed(DavNames.C.Prop).Select(static p => p.Attribute("name") ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = component.Properties.Count - 1; i >= 0; i--)
                if (!keep.Contains(component.Properties[i].Name))
                    component.Properties.RemoveAt(i);
        }

        if (spec.Child(DavNames.C.AllComp) is not null) return;
        var specs = spec.ChildrenNamed(DavNames.C.Comp).ToList();
        for (var i = component.Components.Count - 1; i >= 0; i--) {
            var child = component.Components[i];
            var childSpec = specs.Find(s => string.Equals(s.Attribute("name"), child.Name, StringComparison.OrdinalIgnoreCase));
            if (childSpec is null) component.Components.RemoveAt(i);
            else Prune(child, childSpec);
        }
    }

    private static void RemoveAll(CalendarComponent component, string name) {
        for (var i = component.Properties.Count - 1; i >= 0; i--)
            if (string.Equals(component.Properties[i].Name, name, StringComparison.OrdinalIgnoreCase))
                component.Properties.RemoveAt(i);
    }

    private static void Set(CalendarComponent component, string name, CalendarDateTime value) {
        var property = CalendarProperty.DateTime(name, value);
        for (var i = 0; i < component.Properties.Count; i++) {
            if (!string.Equals(component.Properties[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            component.Properties[i] = property;
            RemoveDuplicates(i);
            return;
        }

        var after = component.Properties.ToList().FindIndex(static p => p.Name == "DTSTART");
        component.Properties.Insert(after < 0 ? component.Properties.Count : after + 1, property);
        return;

        void RemoveDuplicates(int kept) {
            for (var i = component.Properties.Count - 1; i > kept; i--)
                if (string.Equals(component.Properties[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    component.Properties.RemoveAt(i);
        }
    }
}
