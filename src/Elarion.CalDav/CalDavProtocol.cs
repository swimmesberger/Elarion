using System.Text;
using System.Xml;
using Elarion.Abstractions;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.Options;

namespace Elarion.CalDav;

/// <summary>A published calendar the current user sees: its store address and what its source said about it.</summary>
public sealed class CalDavCollection : DavCollection {
    /// <summary>Creates the collection of <paramref name="info"/> at <paramref name="address"/>.</summary>
    public CalDavCollection(CalDavAddress address, PublishedCalendarInfo info) : base(address.ToCollectionAddress(), info.DisplayName) {
        CalendarAddress = address;
        Info = info;
    }

    /// <summary>The calendar's address.</summary>
    public CalDavAddress CalendarAddress { get; }

    /// <summary>What the source returned for the calendar.</summary>
    public PublishedCalendarInfo Info { get; }

    /// <summary>The zone floating times are placed in: the calendar's, or UTC.</summary>
    public TimeZoneInfo TimeZone => Info.TimeZone ?? TimeZoneInfo.Utc;
}

/// <summary>
/// CalDAV on the WebDAV core (RFC 4791, ADR-0111): the calendar home <c>{prefix}/calendars/{user}/</c>, the calendars the
/// registered sources return for the current user, their properties, and the reports <c>calendar-multiget</c>,
/// <c>calendar-query</c> and <c>free-busy-query</c>. Every calendar is read-only.
/// </summary>
internal sealed partial class CalDavProtocol(DavSourceRegistry<CalDavSourceListing> sources, IOptions<CalDavOptions> options, CalendarReader reader, TimeProvider timeProvider)
    : IDavProtocol {
    /// <summary>The collection kind of calendars in the store.</summary>
    public const string Kind = "calendar";

    public string CollectionKind => Kind;

    public string HomeSegment => "calendars";

    public string WellKnownName => "caldav";

    // Never calendar-auto-schedule or calendar-schedule: a read-only server has no inbox or outbox (FR-016).
    public IReadOnlyList<string> ComplianceClasses { get; } = ["calendar-access"];

    public IReadOnlyList<DavName> CollectionResourceTypes { get; } = [DavNames.C.Calendar];

    public IReadOnlyList<DavName> CollectionReports { get; } = [DavNames.C.CalendarMultiget, DavNames.C.CalendarQuery, DavNames.C.FreeBusyQuery];

    public IReadOnlyList<DavProperty> PrincipalProperties => field ??= [
        new(DavNames.C.CalendarHomeSet, false, null, (w, r, _, _) => Href(w, r.Context.HomeHref(this))),
        new(DavNames.C.CalendarUserAddressSet, false, null, (w, r, _, _) => {
            // DAVx5 asks for the account's e-mail when this property holds no mailto: address.
            if (r.Context.User.Email is { Length: > 0 } email) DavXml.TextElement(w, DavNames.D.Href, "mailto:" + email);
            DavXml.TextElement(w, DavNames.D.Href, r.Context.PrincipalHref);
            return ValueTask.CompletedTask;
        }),
        new(DavNames.C.CalendarUserType, false, null, (w, _, _, _) => {
            w.WriteString("INDIVIDUAL");
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> CollectionProperties => field ??= [
        new(DavNames.C.CalendarDescription, true, static r => Calendar(r).Info.Description is not null,
            static (w, r, _, _) => Text(w, Calendar(r).Info.Description!)),
        new(new DavName(DavNames.AppleIcal, "calendar-color"), true, static r => Calendar(r).Info.Color is not null,
            static (w, r, _, _) => Text(w, Calendar(r).Info.Color!)),
        new(DavNames.C.SupportedCalendarComponentSet, true, null, static (w, r, _, _) => {
            foreach (var name in ComponentNames(Calendar(r).Info.Components)) {
                DavXml.StartElement(w, DavNames.C.Comp);
                w.WriteAttributeString("name", name);
                w.WriteEndElement();
            }

            return ValueTask.CompletedTask;
        }),
        new(DavNames.C.CalendarTimezone, false, static r => Calendar(r).Info.TimeZone is not null, WriteCalendarTimeZoneAsync),
        new(DavNames.C.SupportedCalendarData, false, null, static (w, _, _, _) => {
            DavXml.StartElement(w, DavNames.C.CalendarData);
            w.WriteAttributeString("content-type", CalendarWriter.ContentType);
            w.WriteAttributeString("version", "2.0");
            w.WriteEndElement();
            return ValueTask.CompletedTask;
        }),
        new(new DavName(DavNames.CalDav, "supported-collation-set"), false, null, static (w, _, _, _) => {
            foreach (var collation in CalDavFilterEvaluator.Collations)
                DavXml.TextElement(w, new DavName(DavNames.CalDav, "supported-collation"), collation);
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> MemberProperties => field ??= [
        new(DavNames.C.CalendarData, false, static r => r.Content is not null, WriteCalendarDataAsync),
    ];

    public async ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(DavContext context, CancellationToken cancellationToken) {
        var collections = new List<DavCollection>();
        foreach (var source in sources.Sources) {
            // A source the user may not use, behind a closed feature gate, or of a disabled module shows no calendars.
            var listing = await DavSources.InvokeAsync(source, CalDavSourceKind.Value, context.Services, cancellationToken).ConfigureAwait(false);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in listing?.Calendars ?? []) {
                if (info is null) continue;
                DavSources.CheckKey(source.Name, info.Key, keys, CalDavSourceKind.Value);
                collections.Add(new CalDavCollection(new CalDavAddress(source.Name, info.Key), info));
            }
        }

        return collections;
    }

    public ValueTask<DavResponse?> ReportAsync(DavContext context, DavResource collection, DavXmlElement report, CancellationToken cancellationToken) {
        if (collection.Kind == DavResourceKind.Member && report.Is(DavNames.C.CalendarQuery)) return QueryAsync(context, collection, report, cancellationToken);
        if (collection.Kind != DavResourceKind.Collection) return ValueTask.FromResult<DavResponse?>(null);
        if (report.Is(DavNames.C.CalendarMultiget)) return MultigetAsync(context, collection, report, cancellationToken);
        if (report.Is(DavNames.C.CalendarQuery)) return QueryAsync(context, collection, report, cancellationToken);
        if (report.Is(DavNames.C.FreeBusyQuery)) return FreeBusyAsync(context, collection, report, cancellationToken);
        return ValueTask.FromResult<DavResponse?>(null);
    }

    public ValueTask<DavResponse?> HandleOtherAsync(DavContext context, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        FeedAsync(context, segments, cancellationToken);

    internal static CalDavCollection Calendar(DavResource resource) => (CalDavCollection)resource.Collection!;

    internal static IEnumerable<string> ComponentNames(CalendarComponentKinds kinds) {
        if (kinds.HasFlag(CalendarComponentKinds.Event)) yield return "VEVENT";
        if (kinds.HasFlag(CalendarComponentKinds.Todo)) yield return "VTODO";
        if (kinds.HasFlag(CalendarComponentKinds.Journal)) yield return "VJOURNAL";
        if (kinds.HasFlag(CalendarComponentKinds.FreeBusy)) yield return "VFREEBUSY";
        if (kinds.HasFlag(CalendarComponentKinds.Availability)) yield return "VAVAILABILITY";
    }

    private static ValueTask Href(XmlWriter writer, string href) {
        DavXml.TextElement(writer, DavNames.D.Href, href);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Text(XmlWriter writer, string text) {
        writer.WriteString(text);
        return ValueTask.CompletedTask;
    }

    // RFC 4791 §5.2.2: a VCALENDAR holding exactly one VTIMEZONE; DAVx5 makes it the Android calendar's zone.
    private static async ValueTask WriteCalendarTimeZoneAsync(XmlWriter writer, DavResource resource, DavXmlElement? requested,
        CancellationToken cancellationToken) {
        var root = new CalendarComponent("VCALENDAR");
        root.Properties.Add(new CalendarProperty("VERSION", "2.0"));
        root.Properties.Add(CalendarProperty.Text("PRODID", CalendarWriteOptions.DefaultProductId));
        root.Components.Add(CalendarTimeZones.ToComponent(Calendar(resource).Info.TimeZone!));
        using var stream = new MemoryStream();
        await CalendarWriter.WriteAsync(stream, root, cancellationToken: cancellationToken).ConfigureAwait(false);
        writer.WriteString(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    }

    private async ValueTask WriteCalendarDataAsync(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        var includeTimeZones = !string.Equals(resource.Context.Request.Header("CalDAV-Timezones")?.Trim(), "F", StringComparison.OrdinalIgnoreCase);
        var zone = resource.Context.Items.TryGetValue(RequestTimeZoneKey, out var named) && named is TimeZoneInfo requestZone
            ? requestZone
            : Calendar(resource).TimeZone;
        var text = await CalDavCalendarData.RenderAsync(resource.Content!.Value, requested, includeTimeZones, reader, options.Value,
            zone, cancellationToken).ConfigureAwait(false);
        writer.WriteString(text);
    }
}
