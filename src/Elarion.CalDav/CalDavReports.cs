using Elarion.Calendars;
using Elarion.WebDav;

namespace Elarion.CalDav;

internal sealed partial class CalDavProtocol {
    private static DavPropertySelection SelectionOf(DavXmlElement report) {
        if (report.Child(DavNames.D.PropName) is not null) return DavPropertySelection.PropName;
        if (report.Child(DavNames.D.AllProp) is not null) return DavPropertySelection.AllProp;
        return DavPropertySelection.Named(report.Child(DavNames.D.Prop));
    }

    // RFC 4791 §7.9: the named objects, in the order of their hrefs; an href of no object answers 404.
    private async ValueTask<DavResponse?> MultigetAsync(DavContext context, DavResource collection, DavXmlElement report,
        CancellationToken cancellationToken) {
        var hrefs = report.ChildrenNamed(DavNames.D.Href).Select(static h => h.Text.Trim()).Where(static h => h.Length > 0).ToList();
        if (hrefs.Count == 0) return DavResponse.Text(400, "A calendar-multiget needs at least one DAV:href.");
        if (hrefs.Count > context.Options.MaxMultigetHrefs)
            return DavResponse.Text(413, $"A calendar-multiget may name at most {context.Options.MaxMultigetHrefs} objects.");

        var selection = SelectionOf(report);
        var withContent = selection.Names(DavNames.C.CalendarData);
        var calendar = Calendar(collection);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var href in hrefs)
            if (NameIn(collection.Href, href) is { } name)
                names[href] = name;

        var found = new Dictionary<string, DavStoredObject>(StringComparer.Ordinal);
        if (names.Count > 0)
            await foreach (var stored in context.Store.GetObjectsAsync(calendar.Address.Id, names.Values.Distinct().ToList(), withContent, cancellationToken)
                               .ConfigureAwait(false))
                found[stored.Name] = stored;

        var minimal = DavDispatcher.IsMinimal(context.Request);
        return DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            foreach (var href in hrefs) {
                if (names.TryGetValue(href, out var name) && found.TryGetValue(name, out var stored))
                    await DavMultistatus.WriteResourceAsync(output, Member(context, collection, stored), selection, minimal, ct).ConfigureAwait(false);
                else
                    DavMultistatus.WriteStatus(output, href, 404);
            }

            DavMultistatus.End(output);
        });
    }

    // RFC 4791 §7.8: the objects that match the filter. Candidates come from the store's recorded time spans; each is
    // then read and tested exactly.
    private async ValueTask<DavResponse?> QueryAsync(DavContext context, DavResource collection, DavXmlElement report,
        CancellationToken cancellationToken) {
        var filter = report.Child(DavNames.C.Filter);
        if (CalDavFilterEvaluator.Validate(filter) is not null) return DavResponse.Error(403, DavNames.C.ValidFilter);
        var calendar = Calendar(collection);
        var zone = calendar.TimeZone;
        if (report.Child(DavNames.C.Timezone)?.Text is { Length: > 0 } timezoneText) {
            var parsed = await ReadZoneAsync(timezoneText, cancellationToken).ConfigureAwait(false);
            if (parsed is null) return DavResponse.Error(403, new DavName(DavNames.CalDav, "valid-calendar-data"));
            zone = parsed;
        }

        var selection = SelectionOf(report);
        var minimal = DavDispatcher.IsMinimal(context.Request);
        var settings = options.Value;
        var caps = settings.CalendarExpand;
        // A calendar-query asks about the members (Depth 1, as every surveyed client sends); Depth 0 addresses only the
        // collection, which is no calendar object.
        var depthZero = context.Request.Header("Depth")?.Trim() == "0";
        var (start, end) = CandidateRange(filter!);

        return DavResponse.Xml(207, async (output, ct) => {
            DavMultistatus.Start(output);
            if (!depthZero) {
                await foreach (var stored in context.Store.GetCandidatesAsync(calendar.Address.Id, start, end, ct).ConfigureAwait(false)) {
                    using var stream = new MemoryStream(stored.Content.ToArray(), false);
                    var read = await reader.ReadAsync(stream, settings.CalendarRead, ct).ConfigureAwait(false);
                    if (!CalDavFilterEvaluator.Matches(read, filter!, zone, caps)) continue;
                    await DavMultistatus.WriteResourceAsync(output, Member(context, collection, stored), selection, minimal, ct).ConfigureAwait(false);
                }
            }

            DavMultistatus.End(output);
        });
    }

    // RFC 4791 §7.10: the busy time of the calendar's events within the range, as one VFREEBUSY.
    private async ValueTask<DavResponse?> FreeBusyAsync(DavContext context, DavResource collection, DavXmlElement report,
        CancellationToken cancellationToken) {
        if (report.Child(DavNames.C.TimeRange) is not { } range || !CalDavFilterEvaluator.TryParseRange(range, out var start, out var end))
            return DavResponse.Text(400, "A free-busy-query needs a CALDAV:time-range with a start and an end.");
        var calendar = Calendar(collection);
        var settings = options.Value;
        var caps = settings.CalendarExpand with { FloatingTimeZone = calendar.TimeZone };
        var busy = new List<(DateTimeOffset Start, DateTimeOffset End, string Type)>();
        await foreach (var stored in context.Store.GetCandidatesAsync(calendar.Address.Id, start, end, cancellationToken).ConfigureAwait(false)) {
            using var stream = new MemoryStream(stored.Content.ToArray(), false);
            var read = await reader.ReadAsync(stream, settings.CalendarRead, cancellationToken).ConfigureAwait(false);
            var events = read.Events.Where(static e => e.HasValue).Select(static e => (CalendarItem)e.Value!).ToList();
            foreach (var occurrence in CalendarRecurrence.Expand(events, start, end, caps).Occurrences) {
                if (occurrence.Item is not CalendarEvent item || item.Transparency == CalendarTransparency.Transparent
                    || string.Equals(item.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase) || occurrence.StartUtc is not { } s)
                    continue;
                var e = occurrence.EndUtc ?? s;
                if (e <= s) continue;
                var type = string.Equals(item.Status, "TENTATIVE", StringComparison.OrdinalIgnoreCase) ? "BUSY-TENTATIVE" : "BUSY";
                busy.Add((s < start ? start : s, e > end ? end : e, type));
            }
        }

        var freeBusy = new CalendarComponent("VFREEBUSY");
        freeBusy.Properties.Add(CalendarProperty.Timestamp("DTSTAMP", timeProvider.GetUtcNow()));
        freeBusy.Properties.Add(CalendarProperty.Timestamp("DTSTART", start));
        freeBusy.Properties.Add(CalendarProperty.Timestamp("DTEND", end));
        foreach (var group in busy.GroupBy(static b => b.Type).OrderBy(static g => g.Key, StringComparer.Ordinal)) {
            var periods = Merge(group.Select(static b => (b.Start, b.End)));
            freeBusy.Properties.Add(new CalendarProperty("FREEBUSY",
                string.Join(',', periods.Select(static p => Utc(p.Start) + "/" + Utc(p.End))), new CalendarParameter("FBTYPE", group.Key)));
        }

        var root = new CalendarComponent("VCALENDAR");
        root.Properties.Add(new CalendarProperty("VERSION", "2.0"));
        root.Properties.Add(CalendarProperty.Text("PRODID", settings.ProductId));
        root.Components.Add(freeBusy);
        using var output = new MemoryStream();
        await CalendarWriter.WriteAsync(output, root, cancellationToken: cancellationToken).ConfigureAwait(false);
        return DavResponse.Bytes(200, output.ToArray(), CalDavObjects.ContentType);

        static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> periods) {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var period in periods.OrderBy(static p => p.Start)) {
            if (merged.Count > 0 && period.Start <= merged[^1].End) {
                if (period.End > merged[^1].End) merged[^1] = (merged[^1].Start, period.End);
            }
            else {
                merged.Add(period);
            }
        }

        return merged;
    }

    // The range the store selects candidates by: the time-range of the first component filter below VCALENDAR, if any.
    private static (DateTimeOffset? Start, DateTimeOffset? End) CandidateRange(DavXmlElement filter) {
        var top = filter.Child(DavNames.C.CompFilter)!;
        var inner = top.ChildrenNamed(DavNames.C.CompFilter).ToList();
        if (inner.Count != 1 || inner[0].Child(DavNames.C.IsNotDefined) is not null) return (null, null);
        if (inner[0].Attribute("name")?.ToUpperInvariant() is not ("VEVENT" or "VTODO" or "VJOURNAL")) return (null, null);
        if (inner[0].Child(DavNames.C.TimeRange) is not { } range || !CalDavFilterEvaluator.TryParseRange(range, out var start, out var end))
            return (null, null);
        return (start, end);
    }

    private async ValueTask<TimeZoneInfo?> ReadZoneAsync(string text, CancellationToken cancellationToken) {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text), false);
        var read = await reader.ReadAsync(stream, options.Value.CalendarRead, cancellationToken).ConfigureAwait(false);
        var vtimezone = read.Root?.Components.FirstOrDefault(static c => c.Name == "VTIMEZONE");
        return vtimezone is not null && CalendarTimeZones.TryFromComponent(vtimezone, out var zone, out _) ? zone : null;
    }

    private static DavResource Member(DavContext context, DavResource collection, DavMember member) =>
        new(context, DavResourceKind.Member, context.MemberHref(collection.Protocol!, collection.Collection!, member.Name)) {
            Protocol = collection.Protocol, Collection = collection.Collection, Member = member,
        };

    // The member name an href addresses inside the collection, or null for an href elsewhere. Clients send paths or full
    // URLs, escaped as they like.
    private static string? NameIn(string collectionHref, string href) {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" ? absolute.AbsolutePath : href;
        string decodedPath, decodedCollection;
        try {
            decodedPath = Uri.UnescapeDataString(path);
            decodedCollection = Uri.UnescapeDataString(collectionHref);
        }
        catch (UriFormatException) {
            return null;
        }

        if (!decodedPath.StartsWith(decodedCollection, StringComparison.Ordinal)) return null;
        var name = decodedPath[decodedCollection.Length..];
        return name.Length == 0 || name.Contains('/') ? null : name;
    }
}
