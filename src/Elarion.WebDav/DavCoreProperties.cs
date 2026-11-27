using System.Globalization;
using System.Xml;

namespace Elarion.WebDav;

/// <summary>The live properties of RFC 4918, RFC 3744, RFC 5397 and RFC 6578 the core writes for every protocol.</summary>
internal static class DavCoreProperties {
    public static IReadOnlyList<DavProperty> All { get; } = [
        new(DavNames.D.ResourceType, true, null, WriteResourceType),
        new(DavNames.D.DisplayName, true, r => r.Kind is DavResourceKind.Principal or DavResourceKind.Home or DavResourceKind.Collection,
            WriteDisplayName),
        new(DavNames.D.CurrentUserPrincipal, false, null, (w, r, _, _) => Href(w, r.Context.PrincipalHref)),
        new(DavNames.D.PrincipalUrl, false, r => r.Kind == DavResourceKind.Principal, (w, r, _, _) => Href(w, r.Context.PrincipalHref)),
        new(DavNames.D.PrincipalCollectionSet, false, r => r.Kind is DavResourceKind.Root or DavResourceKind.Principal,
            (w, r, _, _) => Href(w, r.Context.PrincipalsHref)),
        new(DavNames.D.Owner, false, r => r.Kind is not (DavResourceKind.Root or DavResourceKind.Principals),
            (w, r, _, _) => Href(w, r.Context.PrincipalHref)),
        new(DavNames.D.CurrentUserPrivilegeSet, false, null, WritePrivileges),
        new(DavNames.D.SupportedReportSet, false, r => r.Kind == DavResourceKind.Collection, WriteSupportedReports),
        new(DavNames.D.SyncToken, false, r => r.Kind == DavResourceKind.Collection && r.State is not null,
            (w, r, _, _) => Text(w, r.State!.SyncToken)),
        // Apple's CTag; its specification allows the sync token as its value, and the counter changes exactly when it must.
        // A live collection has only its source's CTag.
        new(new DavName(DavNames.CalendarServer, "getctag"), false, r => r.Kind == DavResourceKind.Collection && r.CTag is not null,
            (w, r, _, _) => Text(w, r.CTag!)),
        new(DavNames.D.GetETag, true, r => r.Kind == DavResourceKind.Member, (w, r, _, _) => Text(w, r.Member!.ETag)),
        new(DavNames.D.GetContentType, true, r => r.Kind == DavResourceKind.Member, (w, r, _, _) => Text(w, r.Member!.ContentType)),
        // A live listing may not know a member's length without rendering it; the property is then not defined (404).
        new(DavNames.D.GetContentLength, true, r => r.Kind == DavResourceKind.Member && r.Member!.Size is not null,
            (w, r, _, _) => Text(w, r.Member!.Size!.Value.ToString(CultureInfo.InvariantCulture))),
    ];

    /// <summary>Writes <c>&lt;D:href&gt;</c>.</summary>
    public static ValueTask Href(XmlWriter writer, string href) {
        DavXml.TextElement(writer, DavNames.D.Href, href);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Text(XmlWriter writer, string text) {
        writer.WriteString(text);
        return ValueTask.CompletedTask;
    }

    private static ValueTask WriteResourceType(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        if (resource.Kind == DavResourceKind.Member) return ValueTask.CompletedTask;
        DavXml.EmptyElement(writer, DavNames.D.Collection);
        if (resource.Kind == DavResourceKind.Principal) DavXml.EmptyElement(writer, DavNames.D.Principal);
        if (resource.Kind == DavResourceKind.Collection && resource.Protocol is { } protocol)
            foreach (var type in protocol.CollectionResourceTypes)
                DavXml.EmptyElement(writer, type);
        return ValueTask.CompletedTask;
    }

    private static ValueTask WriteDisplayName(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        var name = resource.Kind switch {
            DavResourceKind.Principal => resource.Context.User.Email ?? resource.Context.User.UserId,
            DavResourceKind.Collection => resource.Collection!.DisplayName,
            _ => resource.Protocol?.HomeSegment ?? "",
        };
        writer.WriteString(name);
        return ValueTask.CompletedTask;
    }

    // Read-only, said rather than implied (ADR-0111): read and read-current-user-privilege-set, never write-properties,
    // which Thunderbird before 154 took for write access.
    private static ValueTask WritePrivileges(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        DavXml.StartElement(writer, DavNames.D.Privilege);
        DavXml.EmptyElement(writer, DavNames.D.Read);
        writer.WriteEndElement();
        DavXml.StartElement(writer, DavNames.D.Privilege);
        DavXml.EmptyElement(writer, DavNames.D.ReadCurrentUserPrivilegeSet);
        writer.WriteEndElement();
        return ValueTask.CompletedTask;
    }

    private static ValueTask WriteSupportedReports(XmlWriter writer, DavResource resource, DavXmlElement? requested, CancellationToken cancellationToken) {
        // A live collection has no sync token, so it does not offer sync-collection; clients fall back to the CTag.
        if (resource.Collection?.Live is null) WriteReport(writer, DavNames.D.SyncCollection);
        if (resource.Protocol is { } protocol)
            foreach (var report in protocol.CollectionReports)
                WriteReport(writer, report);
        return ValueTask.CompletedTask;

        static void WriteReport(XmlWriter writer, DavName report) {
            DavXml.StartElement(writer, DavNames.D.SupportedReport);
            DavXml.StartElement(writer, DavNames.D.Report);
            DavXml.EmptyElement(writer, report);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }
}
