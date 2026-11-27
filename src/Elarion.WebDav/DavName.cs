namespace Elarion.WebDav;

/// <summary>The qualified name of a WebDAV element or property: its XML namespace and local name.</summary>
/// <param name="Namespace">The XML namespace, such as <c>DAV:</c>.</param>
/// <param name="LocalName">The local name, such as <c>getetag</c>.</param>
public readonly record struct DavName(string Namespace, string LocalName) {
    /// <inheritdoc />
    public override string ToString() => "{" + Namespace + "}" + LocalName;
}

/// <summary>The XML namespaces and element names WebDAV and its extensions use.</summary>
public static class DavNames {
    /// <summary>The WebDAV namespace, <c>DAV:</c> (RFC 4918).</summary>
    public const string Dav = "DAV:";

    /// <summary>The CalDAV namespace (RFC 4791).</summary>
    public const string CalDav = "urn:ietf:params:xml:ns:caldav";

    /// <summary>The CardDAV namespace (RFC 6352).</summary>
    public const string CardDav = "urn:ietf:params:xml:ns:carddav";

    /// <summary>The Apple CalendarServer namespace, for <c>getctag</c>.</summary>
    public const string CalendarServer = "http://calendarserver.org/ns/";

    /// <summary>The Apple iCal namespace, for <c>calendar-color</c> and <c>calendar-order</c>.</summary>
    public const string AppleIcal = "http://apple.com/ns/ical/";

    /// <summary>Names in the <c>DAV:</c> namespace.</summary>
    internal static class D {
        public static readonly DavName Multistatus = new(Dav, "multistatus");
        public static readonly DavName Response = new(Dav, "response");
        public static readonly DavName Href = new(Dav, "href");
        public static readonly DavName Status = new(Dav, "status");
        public static readonly DavName Propstat = new(Dav, "propstat");
        public static readonly DavName Prop = new(Dav, "prop");
        public static readonly DavName PropFind = new(Dav, "propfind");
        public static readonly DavName AllProp = new(Dav, "allprop");
        public static readonly DavName PropName = new(Dav, "propname");
        public static readonly DavName Include = new(Dav, "include");
        public static readonly DavName Error = new(Dav, "error");
        public static readonly DavName ResourceType = new(Dav, "resourcetype");
        public static readonly DavName Collection = new(Dav, "collection");
        public static readonly DavName Principal = new(Dav, "principal");
        public static readonly DavName DisplayName = new(Dav, "displayname");
        public static readonly DavName CurrentUserPrincipal = new(Dav, "current-user-principal");
        public static readonly DavName PrincipalUrl = new(Dav, "principal-URL");
        public static readonly DavName PrincipalCollectionSet = new(Dav, "principal-collection-set");
        public static readonly DavName Owner = new(Dav, "owner");
        public static readonly DavName CurrentUserPrivilegeSet = new(Dav, "current-user-privilege-set");
        public static readonly DavName Privilege = new(Dav, "privilege");
        public static readonly DavName Read = new(Dav, "read");
        public static readonly DavName ReadCurrentUserPrivilegeSet = new(Dav, "read-current-user-privilege-set");
        public static readonly DavName WriteContent = new(Dav, "write-content");
        public static readonly DavName Bind = new(Dav, "bind");
        public static readonly DavName Unbind = new(Dav, "unbind");
        public static readonly DavName WriteProperties = new(Dav, "write-properties");
        public static readonly DavName SupportedReportSet = new(Dav, "supported-report-set");
        public static readonly DavName SupportedReport = new(Dav, "supported-report");
        public static readonly DavName Report = new(Dav, "report");
        public static readonly DavName SyncToken = new(Dav, "sync-token");
        public static readonly DavName SyncCollection = new(Dav, "sync-collection");
        public static readonly DavName SyncLevel = new(Dav, "sync-level");
        public static readonly DavName Limit = new(Dav, "limit");
        public static readonly DavName NResults = new(Dav, "nresults");
        public static readonly DavName ValidSyncToken = new(Dav, "valid-sync-token");
        public static readonly DavName SyncTraversalSupported = new(Dav, "sync-traversal-supported");
        public static readonly DavName PropFindFiniteDepth = new(Dav, "propfind-finite-depth");
        public static readonly DavName NeedPrivileges = new(Dav, "need-privileges");
        public static readonly DavName Resource = new(Dav, "resource");
        public static readonly DavName GetETag = new(Dav, "getetag");
        public static readonly DavName GetContentType = new(Dav, "getcontenttype");
        public static readonly DavName GetContentLength = new(Dav, "getcontentlength");
    }

    /// <summary>Names in the CalDAV namespace.</summary>
    internal static class C {
        public static readonly DavName Calendar = new(CalDav, "calendar");
        public static readonly DavName CalendarHomeSet = new(CalDav, "calendar-home-set");
        public static readonly DavName CalendarUserAddressSet = new(CalDav, "calendar-user-address-set");
        public static readonly DavName CalendarUserType = new(CalDav, "calendar-user-type");
        public static readonly DavName CalendarDescription = new(CalDav, "calendar-description");
        public static readonly DavName CalendarTimezone = new(CalDav, "calendar-timezone");
        public static readonly DavName SupportedCalendarComponentSet = new(CalDav, "supported-calendar-component-set");
        public static readonly DavName SupportedCalendarData = new(CalDav, "supported-calendar-data");
        public static readonly DavName CalendarData = new(CalDav, "calendar-data");
        public static readonly DavName CalendarMultiget = new(CalDav, "calendar-multiget");
        public static readonly DavName CalendarQuery = new(CalDav, "calendar-query");
        public static readonly DavName FreeBusyQuery = new(CalDav, "free-busy-query");
        public static readonly DavName Comp = new(CalDav, "comp");
        public static readonly DavName Filter = new(CalDav, "filter");
        public static readonly DavName CompFilter = new(CalDav, "comp-filter");
        public static readonly DavName PropFilter = new(CalDav, "prop-filter");
        public static readonly DavName ParamFilter = new(CalDav, "param-filter");
        public static readonly DavName TextMatch = new(CalDav, "text-match");
        public static readonly DavName TimeRange = new(CalDav, "time-range");
        public static readonly DavName IsNotDefined = new(CalDav, "is-not-defined");
        public static readonly DavName Expand = new(CalDav, "expand");
        public static readonly DavName LimitRecurrenceSet = new(CalDav, "limit-recurrence-set");
        public static readonly DavName LimitFreeBusySet = new(CalDav, "limit-freebusy-set");
        public static readonly DavName AllComp = new(CalDav, "allcomp");
        public static readonly DavName AllProp = new(CalDav, "allprop");
        public static readonly DavName Prop = new(CalDav, "prop");
        public static readonly DavName Timezone = new(CalDav, "timezone");
        public static readonly DavName ValidFilter = new(CalDav, "valid-filter");
        public static readonly DavName SupportedFilter = new(CalDav, "supported-filter");
    }

    /// <summary>Names in the CardDAV namespace.</summary>
    internal static class Card {
        public static readonly DavName AddressBook = new(CardDav, "addressbook");
        public static readonly DavName AddressBookHomeSet = new(CardDav, "addressbook-home-set");
        public static readonly DavName AddressBookDescription = new(CardDav, "addressbook-description");
        public static readonly DavName SupportedAddressData = new(CardDav, "supported-address-data");
        public static readonly DavName AddressDataType = new(CardDav, "address-data-type");
        public static readonly DavName AddressData = new(CardDav, "address-data");
        public static readonly DavName AddressBookMultiget = new(CardDav, "addressbook-multiget");
        public static readonly DavName AddressBookQuery = new(CardDav, "addressbook-query");
        public static readonly DavName Filter = new(CardDav, "filter");
        public static readonly DavName PropFilter = new(CardDav, "prop-filter");
        public static readonly DavName ParamFilter = new(CardDav, "param-filter");
        public static readonly DavName TextMatch = new(CardDav, "text-match");
        public static readonly DavName IsNotDefined = new(CardDav, "is-not-defined");
        public static readonly DavName Prop = new(CardDav, "prop");
        public static readonly DavName AllProp = new(CardDav, "allprop");
        public static readonly DavName Limit = new(CardDav, "limit");
        public static readonly DavName NResults = new(CardDav, "nresults");
        public static readonly DavName SupportedCollationSet = new(CardDav, "supported-collation-set");
        public static readonly DavName SupportedCollation = new(CardDav, "supported-collation");
        public static readonly DavName SupportedFilter = new(CardDav, "supported-filter");
    }
}
