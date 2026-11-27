using System.Xml;

namespace Elarion.WebDav;

/// <summary>
/// A DAV protocol built on the WebDAV core (CalDAV now, CardDAV later): the home segment it owns, the collections the
/// current user sees, the properties and reports it adds. Registered in DI by the protocol package; the dispatcher and
/// the host know no protocol by name (ADR-0111).
/// </summary>
public interface IDavProtocol {
    /// <summary>The collection kind the protocol publishes into the store, such as <c>calendar</c>.</summary>
    string CollectionKind { get; }

    /// <summary>The first URL segment of the protocol's homes, such as <c>calendars</c> for <c>/dav/calendars/{user}/</c>.</summary>
    string HomeSegment { get; }

    /// <summary>The well-known name of RFC 6764, such as <c>caldav</c> for <c>/.well-known/caldav</c>.</summary>
    string WellKnownName { get; }

    /// <summary>The compliance classes of the <c>DAV</c> header, such as <c>calendar-access</c>.</summary>
    IReadOnlyList<string> ComplianceClasses { get; }

    /// <summary>The element(s) the protocol's collections add to <c>resourcetype</c>, such as <c>C:calendar</c>.</summary>
    IReadOnlyList<DavName> CollectionResourceTypes { get; }

    /// <summary>The reports the protocol answers on its collections, listed in <c>supported-report-set</c>.</summary>
    IReadOnlyList<DavName> CollectionReports { get; }

    /// <summary>Properties the protocol adds to the principal, such as <c>calendar-home-set</c>.</summary>
    IReadOnlyList<DavProperty> PrincipalProperties { get; }

    /// <summary>Properties the protocol adds to its collections, such as <c>calendar-color</c>.</summary>
    IReadOnlyList<DavProperty> CollectionProperties { get; }

    /// <summary>Properties the protocol adds to the members of its collections in reports, such as <c>calendar-data</c>.</summary>
    IReadOnlyList<DavProperty> MemberProperties { get; }

    /// <summary>
    /// The collections of this protocol the current user may see, each resolved by the protocol's own access rule. Called
    /// at most once per request; the dispatcher caches the answer in <paramref name="context"/>.
    /// </summary>
    ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(DavContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Answers a <c>REPORT</c> on one of the protocol's collections, or returns <see langword="null"/> for a report the
    /// protocol does not know (the dispatcher then answers <c>403</c> with <c>supported-report</c>).
    /// </summary>
    ValueTask<DavResponse?> ReportAsync(DavContext context, DavResource collection, DavXmlElement report, CancellationToken cancellationToken);

    /// <summary>
    /// Answers a request below the prefix whose first segment is neither <c>principals</c> nor a home segment (the
    /// CalDAV feeds use <c>feeds</c>), or returns <see langword="null"/> when the path is not the protocol's.
    /// </summary>
    ValueTask<DavResponse?> HandleOtherAsync(DavContext context, IReadOnlyList<string> segments, CancellationToken cancellationToken);
}

/// <summary>
/// A published collection the current user may see, as a protocol resolved it. A protocol derives from it to carry its
/// own metadata (CalDAV keeps the published calendar's color, time zone and component kinds).
/// </summary>
public class DavCollection {
    /// <summary>Creates a collection.</summary>
    public DavCollection(DavCollectionAddress address, string displayName) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(displayName);
        Address = address;
        DisplayName = displayName;
    }

    /// <summary>The collection's address in the store.</summary>
    public DavCollectionAddress Address { get; }

    /// <summary>The name shown to the user.</summary>
    public string DisplayName { get; }

    /// <summary>The URL segment, <c>{source}.{key}</c>.</summary>
    public string Segment => Address.Segment;
}

/// <summary>Writes the content of a property element for a resource.</summary>
/// <param name="writer">The writer, positioned inside the property's element.</param>
/// <param name="resource">The resource the property is written for.</param>
/// <param name="requested">The property element as the client requested it (with children for parameterised properties
/// such as <c>calendar-data</c>), or <see langword="null"/> for <c>allprop</c>.</param>
/// <param name="cancellationToken">Cancels the write.</param>
public delegate ValueTask DavPropertyWriter(XmlWriter writer, DavResource resource, DavXmlElement? requested,
    CancellationToken cancellationToken);

/// <summary>
/// A live property: its name, whether <c>allprop</c> returns it, whether a resource has it, and how its value is written.
/// A property a resource does not have answers <c>404</c> inside the <c>multistatus</c> (spec 0006 FR-017).
/// </summary>
/// <param name="Name">The property's qualified name.</param>
/// <param name="InAllProp">Whether <c>allprop</c> returns it.</param>
/// <param name="IsDefined">Whether a resource has the property; <see langword="null"/> for every resource of its table.</param>
/// <param name="Write">Writes its value.</param>
public sealed record DavProperty(DavName Name, bool InAllProp, Func<DavResource, bool>? IsDefined, DavPropertyWriter Write);
