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

    /// <summary>
    /// The first URL segment of the protocol's homes, such as <c>calendars</c> for <c>/dav/calendars/{user}/</c>. It is a
    /// URL contract: apps store the home and collection URLs, so it never changes once devices synced.
    /// </summary>
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

    /// <summary>
    /// The representation a <c>GET</c> of <paramref name="member"/> answers when the request asks for another one than the
    /// stored bytes (CardDAV converts a vCard 3.0 card to 4.0 by the <c>Accept</c> header), or <see langword="null"/> for
    /// the stored bytes. The answer carries the stored object's ETag either way.
    /// </summary>
    ValueTask<DavRepresentation?> GetRepresentationAsync(DavContext context, DavStoredObject member, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DavRepresentation?>(null);
}

/// <summary>A representation of a member other than its stored bytes, which a <c>GET</c> answers.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="ContentType">The media type.</param>
public sealed record DavRepresentation(ReadOnlyMemory<byte> Content, string ContentType);

/// <summary>
/// A collection the current user may see, as a protocol resolved it: a published one, whose members the store holds,
/// or a live one, whose members <see cref="Live"/> reads per request (ADR-0113, amendment 2026-11-27). A protocol
/// derives from it to carry its own metadata (CalDAV keeps the published calendar's color, time zone and component kinds).
/// </summary>
public class DavCollection {
    /// <summary>The longest <see cref="Segment"/>: 128 characters.</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>Creates a collection.</summary>
    public DavCollection(DavCollectionAddress address, string displayName) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(displayName);
        Address = address;
        DisplayName = displayName;
        Segment = address.Segment;
    }

    /// <summary>The collection's address in the store; for a live collection, only its identity.</summary>
    public DavCollectionAddress Address { get; }

    /// <summary>The name shown to the user.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// The URL segment of the collection in the user's home: <c>{source}.{key}</c> unless the protocol sets another. It
    /// is a URL contract: apps store it, so changing it makes every synced device sync the collection anew. It is matched
    /// exactly (ordinal) and never changes the store address or the sync token.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid segment (<see cref="IsValidSegment"/>).</exception>
    public string Segment {
        get;
        init {
            if (!IsValidSegment(value))
                throw new ArgumentException(
                    $"The collection segment '{value}' must be 1 to {MaxSegmentLength} ASCII letters, digits, '-', '_', '.', '~' or '@', "
                    + "and not '.' or '..'.", nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// The live source of the collection's members, or <see langword="null"/> for a published collection, whose members
    /// the store holds. A live collection is answered by its CTag: it has no sync token, and <c>sync-collection</c> is
    /// neither listed nor answered on it.
    /// </summary>
    public IDavLiveCollection? Live { get; init; }

    /// <summary>
    /// Whether <paramref name="value"/> is a valid collection segment: 1 to 128 ASCII letters, digits, <c>-</c>, <c>_</c>,
    /// <c>.</c>, <c>~</c> or <c>@</c>, and neither <c>.</c> nor <c>..</c>, so it is one path segment that needs no escaping.
    /// </summary>
    public static bool IsValidSegment(string? value) {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSegmentLength || value is "." or "..") return false;
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '~' or '@'))
                return false;
        return true;
    }
}

/// <summary>What a live collection is asked for members for, so it can leave heavy parts out where they are not shown.</summary>
public enum DavContentPurpose {
    /// <summary>
    /// The member's name, entity tag and media type only (a <c>PROPFIND</c> of one member, a refused write, a report that
    /// asks for no data): the content may be empty.
    /// </summary>
    Metadata,

    /// <summary>A <c>GET</c> or <c>HEAD</c> of one member: the full content.</summary>
    Get,

    /// <summary>
    /// A multiget report, or a <c>PROPFIND</c> that names a member's data property (<c>address-data</c>,
    /// <c>calendar-data</c>): the full content of each member asked for, as a <c>GET</c> answers it, since a client keeps
    /// it under the member's entity tag.
    /// </summary>
    Multiget,

    /// <summary>
    /// A query report: the content every member is matched against and answered with. People search with it; a source
    /// may leave out what no filter reads, such as photos.
    /// </summary>
    Query,
}

/// <summary>
/// The members of a live collection, read per request as the current user instead of from the store (ADR-0113,
/// amendment 2026-11-27): for collections whose content differs per viewer by rules that change without the content
/// changing (row-level visibility), which one published collection per viewer could follow only with a projection of
/// its own.
/// </summary>
/// <remarks>
/// <para>
/// A live collection is answered by its CTag: <c>getctag</c> carries it, <c>sync-token</c> is not defined, and
/// <c>sync-collection</c> is neither in <c>supported-report-set</c> nor answered (<c>403</c> with
/// <c>supported-report</c>), so clients compare the CTag and then the members' entity tags. The CTag must change
/// whenever the listing would, including when the current user's visibility changes; a hash over the listing is always
/// correct.
/// </para>
/// <para>
/// A member's entity tag must be the same in the listing and with the content, and must change exactly when the content
/// does, or clients fetch the member on every sync or keep a stale copy.
/// </para>
/// </remarks>
public interface IDavLiveCollection {
    /// <summary>The collection's CTag for the current user.</summary>
    ValueTask<string> GetCTagAsync(DavContext context, CancellationToken cancellationToken);

    /// <summary>Every member the current user sees, without content; <see cref="DavMember.Size"/> may be unknown.</summary>
    IAsyncEnumerable<DavMember> ListMembersAsync(DavContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The members named <paramref name="names"/> that exist and the current user sees, or all of them when
    /// <paramref name="names"/> is <see langword="null"/>; with content unless <paramref name="purpose"/> is
    /// <see cref="DavContentPurpose.Metadata"/>. A member that was not asked for is ignored.
    /// </summary>
    IAsyncEnumerable<DavStoredObject> GetMembersAsync(DavContext context, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
        CancellationToken cancellationToken);
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
