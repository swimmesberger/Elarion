using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Elarion.Calendars;
using Elarion.WebDav;

namespace Elarion.CalDav;

/// <summary>
/// A CalDAV client (ADR-0111, spec 0006 FR-037–FR-043): discovers an account from a URL, lists its calendars, syncs a
/// calendar one step at a time and writes objects with preconditions. What a server sends is input: a refused credential,
/// a missing calendar, an answer over a limit or not valid WebDAV is a <see cref="CalDavResult{T}"/> with a
/// <see cref="CalDavError"/>, and an object that is not valid iCalendar comes back with the <c>calendar.*</c> errors of its
/// read result. Only cancellation, misconfiguration and transport failures throw.
/// </summary>
/// <remarks>
/// Give the client an <see cref="HttpClient"/> whose handler does not follow redirects (<c>AllowAutoRedirect = false</c>):
/// the client follows them itself, keeping the method, the body and the credentials, which an automatic redirect drops.
/// The client holds no state between calls; the application stores each step's <see cref="CalDavSyncStep.State"/> and
/// schedules the steps.
/// </remarks>
/// <example>
/// <code>
/// var client = new CalDavClient(httpClient, new CalDavClientOptions {
///     Authorization = ct => ValueTask.FromResult&lt;AuthenticationHeaderValue?&gt;(new AuthenticationHeaderValue("Bearer", token)),
/// });
/// var account = await client.DiscoverAsync(new Uri("https://caldav.example.com"), ct);
/// if (!account.HasValue) return account.Error;
/// var step = await client.SyncAsync(account.Value.Calendars[0], previousState, ct);
/// // step.Value.Changed, step.Value.Removed; store step.Value.State for the next step
/// </code>
/// </example>
public sealed class CalDavClient {
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");
    private const string Ns = "xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:ICAL=\"http://apple.com/ns/ical/\"";

    private readonly HttpClient _http;
    private readonly CalDavClientOptions _options;
    private readonly CalendarReader _reader;

    /// <summary>Creates a client over <paramref name="httpClient"/>, which the application owns.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public CalDavClient(HttpClient httpClient, CalDavClientOptions? options = null, CalendarReader? reader = null) {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _options = options ?? new CalDavClientOptions();
        _options.Validate();
        _reader = reader ?? new CalendarReader();
    }

    /// <summary>
    /// Discovers the account at <paramref name="baseUrl"/> (FR-037): <c>current-user-principal</c> on the URL, else through
    /// <c>/.well-known/caldav</c>, then <c>calendar-home-set</c> on the principal (depth 0; iCloud refuses depth 1 there), then
    /// the calendars of the home.
    /// </summary>
    public async Task<CalDavResult<CalDavAccount>> DiscoverAsync(Uri baseUrl, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri) throw new ArgumentException("The base URL must be absolute.", nameof(baseUrl));

        var principalBody = $"<D:propfind {Ns}><D:prop><D:current-user-principal/><D:resourcetype/></D:prop></D:propfind>";
        Uri? principal = null;
        CalDavError? firstError = null;
        var answeredWithoutPrincipal = false;
        foreach (var candidate in new[] { baseUrl, new Uri(baseUrl, "/.well-known/caldav") }) {
            var answer = await SendMultistatusAsync(PropFind, candidate, principalBody, "0", cancellationToken).ConfigureAwait(false);
            if (!answer.HasValue) {
                if (answer.Error.Code == CalDavErrorCodes.Unauthorized) return CalDavResult<CalDavAccount>.Failure(answer.Error);
                firstError ??= answer.Error;
                continue;
            }

            principal = answer.Value.Responses.Select(r => r.Href(DavNames.D.CurrentUserPrincipal)).FirstOrDefault(static h => h is not null);
            if (principal is not null) break;
            answeredWithoutPrincipal |= candidate == baseUrl;
        }

        var homes = principal is null ? null : await HomesOfAsync(principal, cancellationToken).ConfigureAwait(false);
        if (homes is { HasValue: false }) return CalDavResult<CalDavAccount>.Failure(homes.Error);
        if (principal is null) {
            if (!answeredWithoutPrincipal)
                return CalDavResult<CalDavAccount>.Failure(firstError ?? Error(CalDavErrorCodes.NotFound, "The server names no current-user-principal.", null, baseUrl));
            // Servers without current-user-principal (Synology, DavMail): the given URL, which answered as WebDAV, is the
            // principal when it names a home.
            homes = await HomesOfAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            if (!homes.HasValue || homes.Value.Homes.Count == 0) {
                if (homes is { HasValue: false, Error.Code: CalDavErrorCodes.Unauthorized }) return CalDavResult<CalDavAccount>.Failure(homes.Error);
                // A URL that answered as WebDAV but names no principal is "not found", whatever the well-known URL said.
                return CalDavResult<CalDavAccount>.Failure(Error(CalDavErrorCodes.NotFound, "The server names no current-user-principal.", null, baseUrl));
            }

            principal = baseUrl;
        }

        // A principal without calendar-home-set (Nextcloud's public shares) holds its calendars itself.
        var homeSet = homes!.Value!;
        var homeUrls = homeSet.Homes.Count > 0 ? homeSet.Homes : [principal];
        var calendars = new List<CalDavCalendar>();
        foreach (var home in homeUrls) {
            var listed = await ListCalendarsAsync(home, cancellationToken).ConfigureAwait(false);
            if (!listed.HasValue) return CalDavResult<CalDavAccount>.Failure(listed.Error);
            foreach (var calendar in listed.Value)
                if (calendars.All(known => known.Url != calendar.Url))
                    calendars.Add(calendar);
        }

        return CalDavResult<CalDavAccount>.Success(new CalDavAccount {
            PrincipalUrl = principal,
            HomeUrl = homeUrls[0],
            HomeUrls = homeUrls,
            DisplayName = homeSet.DisplayName,
            Calendars = calendars,
        });
    }

    private sealed record HomeSet(IReadOnlyList<Uri> Homes, string? DisplayName);

    // The hrefs of calendar-home-set on the principal (depth 0; iCloud refuses depth 1 there), every one of them.
    private async Task<CalDavResult<HomeSet>> HomesOfAsync(Uri principal, CancellationToken cancellationToken) {
        var answer = await SendMultistatusAsync(PropFind, principal,
            $"<D:propfind {Ns}><D:prop><C:calendar-home-set/><D:displayname/></D:prop></D:propfind>", "0", cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<HomeSet>.Failure(answer.Error);
        var homes = new List<Uri>();
        string? displayName = null;
        foreach (var response in answer.Value.Responses) {
            displayName ??= response.Text(DavNames.D.DisplayName);
            foreach (var href in response.Prop(DavNames.C.CalendarHomeSet)?.ChildrenNamed(DavNames.D.Href) ?? [])
                if (MultistatusResponse.Resolve(answer.Value.BaseUrl, href.Text.Trim()) is { } url && !homes.Contains(url))
                    homes.Add(url);
        }

        return CalDavResult<HomeSet>.Success(new HomeSet(homes, displayName));
    }

    /// <summary>The calendars of the calendar home at <paramref name="homeUrl"/>, with their names, colors, component kinds and whether they are writable.</summary>
    public async Task<CalDavResult<IReadOnlyList<CalDavCalendar>>> ListCalendarsAsync(Uri homeUrl, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(homeUrl);
        var body = $"<D:propfind {Ns}><D:prop><D:resourcetype/><D:displayname/><ICAL:calendar-color/><C:calendar-description/>"
                   + "<C:supported-calendar-component-set/><D:current-user-privilege-set/><D:supported-report-set/></D:prop></D:propfind>";
        var answer = await SendMultistatusAsync(PropFind, homeUrl, body, "1", cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<IReadOnlyList<CalDavCalendar>>.Failure(answer.Error);

        var calendars = new List<CalDavCalendar>();
        foreach (var response in answer.Value.Responses) {
            if (response.Prop(DavNames.D.ResourceType)?.Child(DavNames.C.Calendar) is null) continue;
            var privileges = response.Prop(DavNames.D.CurrentUserPrivilegeSet);
            var writable = privileges is null || privileges.ChildrenNamed(DavNames.D.Privilege).SelectMany(static p => p.Children)
                .Any(static p => p.Name.Namespace == DavNames.Dav && p.Name.LocalName is "write" or "write-content" or "all" or "bind");
            var components = CalendarComponentKinds.None;
            foreach (var comp in response.Prop(DavNames.C.SupportedCalendarComponentSet)?.ChildrenNamed(DavNames.C.Comp) ?? [])
                components |= (comp.Attribute("name") ?? "").ToUpperInvariant() switch {
                    "VEVENT" => CalendarComponentKinds.Event,
                    "VTODO" => CalendarComponentKinds.Todo,
                    "VJOURNAL" => CalendarComponentKinds.Journal,
                    "VFREEBUSY" => CalendarComponentKinds.FreeBusy,
                    "VAVAILABILITY" => CalendarComponentKinds.Availability,
                    _ => CalendarComponentKinds.None,
                };
            var reports = response.Prop(DavNames.D.SupportedReportSet);
            calendars.Add(new CalDavCalendar {
                Url = WithSlash(response.Url),
                DisplayName = response.Text(DavNames.D.DisplayName),
                Color = response.Text(new DavName(DavNames.AppleIcal, "calendar-color")),
                Description = response.Text(DavNames.C.CalendarDescription),
                Components = components == CalendarComponentKinds.None ? CalendarComponentKinds.Event | CalendarComponentKinds.Todo : components,
                IsWritable = writable,
                SupportsSyncCollection = reports is not null && Contains(reports, DavNames.D.SyncCollection),
            });
        }

        return CalDavResult<IReadOnlyList<CalDavCalendar>>.Success(calendars);

        static bool Contains(DavXmlElement element, DavName name) => element.Is(name) || element.Children.Any(child => Contains(child, name));
    }

    /// <summary>
    /// Runs one sync step of <paramref name="calendar"/> (FR-038, FR-039): with <c>sync-collection</c> when the server offers
    /// it, else, or when the server refuses the token, by comparing the ETags of a full listing; the changed objects are
    /// fetched with <c>calendar-multiget</c> and read with <see cref="CalendarReader"/>. On an error the caller keeps its
    /// previous state.
    /// </summary>
    /// <param name="calendar">The calendar, as <see cref="ListCalendarsAsync"/> returned it.</param>
    /// <param name="state">The previous step's <see cref="CalDavSyncStep.State"/>, or <see langword="null"/> for the first step.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not a state this client wrote.</exception>
    public async Task<CalDavResult<CalDavSyncStep>> SyncAsync(CalDavCalendar calendar, string? state, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendar);
        var previous = CalDavSyncState.Parse(state);

        Listing listing;
        if (calendar.SupportsSyncCollection || previous.Token is not null) {
            var answer = await SyncCollectionAsync(calendar, previous.Token, cancellationToken).ConfigureAwait(false);
            // An expired or unknown token: start over with a full listing and compare ETags (Evolution's and DAVx5's rule). Ox
            // answers a bare 400; a 500 stays an error, since retrying would hide an outage behind a second failure.
            if (!answer.HasValue && previous.Token is not null && answer.Error.StatusCode is 400 or 403 or 409 or 412)
                answer = await SyncCollectionAsync(calendar, null, cancellationToken).ConfigureAwait(false);
            if (!answer.HasValue && answer.Error.StatusCode is 403 or 405 or 501 && previous.Token is null)
                answer = await ListByETagAsync(calendar, previous, cancellationToken).ConfigureAwait(false);
            if (!answer.HasValue) return CalDavResult<CalDavSyncStep>.Failure(answer.Error);
            listing = answer.Value;
        }
        else {
            var answer = await ListByETagAsync(calendar, previous, cancellationToken).ConfigureAwait(false);
            if (!answer.HasValue) return CalDavResult<CalDavSyncStep>.Failure(answer.Error);
            listing = answer.Value;
        }

        if (listing.Unchanged) return CalDavResult<CalDavSyncStep>.Success(new CalDavSyncStep { Changed = [], Removed = [], State = previous.Serialize() });

        var etags = new Dictionary<string, string>(previous.ETags, StringComparer.Ordinal);
        var removed = new List<string>();
        if (listing.IsFull) {
            foreach (var name in etags.Keys.Where(name => !listing.Present.ContainsKey(name)).ToList()) {
                etags.Remove(name);
                removed.Add(name);
            }
        }

        foreach (var name in listing.Removed)
            if (etags.Remove(name) || !listing.IsFull)
                removed.Add(name);

        var wanted = listing.Present.Where(pair => pair.Value.ETag is null || !etags.TryGetValue(pair.Key, out var known) || known != pair.Value.ETag)
            .Select(static pair => (pair.Key, pair.Value.Url)).ToList();
        var deferred = wanted.Count > _options.MaxObjectsPerStep;
        if (deferred) wanted = wanted[.._options.MaxObjectsPerStep];

        var changed = new List<CalDavObject>();
        foreach (var batch in wanted.Chunk(_options.MultigetBatchSize)) {
            var fetched = await MultigetAsync(calendar, batch, cancellationToken).ConfigureAwait(false);
            if (!fetched.HasValue) return CalDavResult<CalDavSyncStep>.Failure(fetched.Error);
            foreach (var (name, item) in fetched.Value) {
                if (item is null) {
                    // Zimbra answers a deleted object in a multiget with 404: it is a removal.
                    if (etags.Remove(name) || !removed.Contains(name)) removed.Add(name);
                    continue;
                }

                changed.Add(item);
                etags[name] = item.ETag ?? listing.Present.GetValueOrDefault(name)?.ETag ?? "";
            }
        }

        // A deferred step keeps the old token, so the next step lists the same changes and fetches what is left; a truncated
        // answer's token continues after it.
        var next = new CalDavSyncState {
            Token = deferred ? previous.Token : listing.Token,
            CTag = deferred ? previous.CTag : listing.CTag ?? previous.CTag,
            ETags = etags,
        };
        return CalDavResult<CalDavSyncStep>.Success(new CalDavSyncStep {
            Changed = changed, Removed = removed.Distinct(StringComparer.Ordinal).ToList(), State = next.Serialize(),
            // A truncated answer whose token did not move would page forever (python-caldav's sync-token.limit quirk).
            HasMore = deferred || (listing.Truncated && listing.Token != previous.Token),
        });
    }

    /// <summary>
    /// Writes <paramref name="calendarObject"/> as <paramref name="name"/> in <paramref name="calendar"/> (FR-042):
    /// <c>If-Match</c> with <paramref name="ifMatch"/>, or <c>If-None-Match: *</c> to create. A failed precondition is the
    /// error <see cref="CalDavErrorCodes.Conflict"/>.
    /// </summary>
    public async Task<CalDavResult<CalDavWriteOutcome>> PutAsync(CalDavCalendar calendar, string name, CalendarObject calendarObject, string? ifMatch,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(calendarObject);
        using var stream = new MemoryStream();
        var writer = CalendarWriter.Create(stream, null, new CalendarWriteOptions { Timestamp = _options.TimeProvider.GetUtcNow() });
        await using (writer.ConfigureAwait(false)) await writer.AddAsync(calendarObject, cancellationToken).ConfigureAwait(false);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/calendar; charset=utf-8");
        var answer = await SendAsync(HttpMethod.Put, MemberUrl(calendar, name), content, null, ifMatch, cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<CalDavWriteOutcome>.Failure(answer.Error);
        return CalDavResult<CalDavWriteOutcome>.Success(new CalDavWriteOutcome(StrongETag(answer.Value.ETag)));
    }

    /// <summary>Deletes <paramref name="name"/> from <paramref name="calendar"/> with <c>If-Match</c> when <paramref name="ifMatch"/> is given (FR-042).</summary>
    public async Task<CalDavResult<CalDavWriteOutcome>> DeleteAsync(CalDavCalendar calendar, string name, string? ifMatch,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var answer = await SendAsync(HttpMethod.Delete, MemberUrl(calendar, name), null, null, ifMatch ?? "", cancellationToken).ConfigureAwait(false);
        return answer.HasValue
            ? CalDavResult<CalDavWriteOutcome>.Success(new CalDavWriteOutcome(null))
            : CalDavResult<CalDavWriteOutcome>.Failure(answer.Error);
    }

    // A listed member: its ETag, and its URL as the server spelled it, which a multiget asks for again (Robur lists
    // "a@b.ics" and answers 404 for "a%40b.ics").
    private sealed record Member(string? ETag, Uri Url);

    private sealed record Listing(bool IsFull, Dictionary<string, Member> Present, List<string> Removed, string? Token, string? CTag, bool Truncated,
        bool Unchanged = false);

    private async Task<CalDavResult<Listing>> SyncCollectionAsync(CalDavCalendar calendar, string? token, CancellationToken cancellationToken) {
        var body = $"<D:sync-collection {Ns}><D:sync-token>{Escape(token ?? "")}</D:sync-token><D:sync-level>1</D:sync-level>"
                   + "<D:prop><D:getetag/></D:prop></D:sync-collection>";
        var answer = await SendMultistatusAsync(Report, calendar.Url, body, "1", cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<Listing>.Failure(answer.Error);
        var present = new Dictionary<string, Member>(StringComparer.Ordinal);
        var removed = new List<string>();
        var truncated = false;
        foreach (var response in answer.Value.Responses) {
            if (IsSelf(calendar, response.Url)) {
                truncated |= response.Status == 507;
                continue;
            }

            var name = NameOf(response.Url);
            if (name is null) continue;
            // The same href twice in one answer (Cyrus, Stalwart after a re-create): the last entry decides.
            present.Remove(name);
            removed.Remove(name);
            if (response.Status is 404 or 410 || (response.Status is null && !response.HasOkPropstat)) removed.Add(name);
            else present[name] = new Member(response.Text(DavNames.D.GetETag), response.Url);
        }

        var newToken = answer.Value.Root.Child(DavNames.D.SyncToken)?.Text.Trim();
        if (string.IsNullOrEmpty(newToken))
            return CalDavResult<Listing>.Failure(Error(CalDavErrorCodes.InvalidResponse, "The sync-collection answer holds no sync-token.", 207, calendar.Url));
        return CalDavResult<Listing>.Success(new Listing(token is null, present, removed, newToken, null, truncated));
    }

    // Without sync-collection: the CTag says whether anything changed; a depth-1 listing of ETags says what.
    private async Task<CalDavResult<Listing>> ListByETagAsync(CalDavCalendar calendar, CalDavSyncState previous, CancellationToken cancellationToken) {
        var ctagAnswer = await SendMultistatusAsync(PropFind, calendar.Url, $"<D:propfind {Ns}><D:prop><CS:getctag/></D:prop></D:propfind>", "0",
            cancellationToken).ConfigureAwait(false);
        if (!ctagAnswer.HasValue) return CalDavResult<Listing>.Failure(ctagAnswer.Error);
        var ctag = ctagAnswer.Value.Responses.Select(static r => r.Text(new DavName(DavNames.CalendarServer, "getctag"))).FirstOrDefault(static t => t is not null);
        if (ctag is not null && ctag == previous.CTag && previous.Token is null)
            return CalDavResult<Listing>.Success(new Listing(true, [], [], null, ctag, false, Unchanged: true));

        var answer = await SendMultistatusAsync(PropFind, calendar.Url, $"<D:propfind {Ns}><D:prop><D:getetag/><D:resourcetype/></D:prop></D:propfind>", "1",
            cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<Listing>.Failure(answer.Error);
        var present = new Dictionary<string, Member>(StringComparer.Ordinal);
        foreach (var response in answer.Value.Responses) {
            if (IsSelf(calendar, response.Url) || response.Prop(DavNames.D.ResourceType)?.Child(DavNames.D.Collection) is not null) continue;
            if (NameOf(response.Url) is { } name && response.HasOkPropstat) present[name] = new Member(response.Text(DavNames.D.GetETag), response.Url);
        }

        return CalDavResult<Listing>.Success(new Listing(true, present, [], null, ctag, false));
    }

    // The named members, asked for by the URL the server listed; a member the answer leaves out (sabre/dav, so Nextcloud,
    // Baikal, DAViCal) or answers 404 is a removal, and an href answered twice is one object, the last.
    private async Task<CalDavResult<List<(string Name, CalDavObject? Item)>>> MultigetAsync(CalDavCalendar calendar, (string Name, Uri Url)[] members,
        CancellationToken cancellationToken) {
        var hrefs = string.Concat(members.Select(static member => "<D:href>" + Escape(member.Url.AbsolutePath) + "</D:href>"));
        var body = $"<C:calendar-multiget {Ns}><D:prop><D:getetag/><C:calendar-data/></D:prop>{hrefs}</C:calendar-multiget>";
        var answer = await SendMultistatusAsync(Report, calendar.Url, body, "1", cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<List<(string, CalDavObject?)>>.Failure(answer.Error);

        var results = new Dictionary<string, CalDavObject?>(StringComparer.Ordinal);
        var wanted = members.Select(static member => member.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var response in answer.Value.Responses) {
            if (NameOf(response.Url) is not { } name || !wanted.Contains(name)) continue;
            if (response.Status is 404 or 410 || response.Prop(DavNames.C.CalendarData) is not { } data) {
                if (response.Status is 404 or 410 || !response.HasOkPropstat) results[name] = null;
                continue;
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data.Text), false);
            var read = await _reader.ReadAsync(stream, _options.CalendarRead, cancellationToken).ConfigureAwait(false);
            results[name] = new CalDavObject(name, response.Url, response.Text(DavNames.D.GetETag), read);
        }

        foreach (var name in wanted)
            results.TryAdd(name, null);
        return CalDavResult<List<(string, CalDavObject?)>>.Success([.. members.Select(member => (member.Name, results[member.Name]))]);
    }

    private sealed record Answer(int Status, string? ETag, byte[] Body, Uri Url);

    private async Task<CalDavResult<Multistatus>> SendMultistatusAsync(HttpMethod method, Uri url, string body, string depth, CancellationToken cancellationToken) {
        var content = new StringContent(body, Encoding.UTF8, "application/xml");
        var answer = await SendAsync(method, url, content, depth, null, cancellationToken).ConfigureAwait(false);
        if (!answer.HasValue) return CalDavResult<Multistatus>.Failure(answer.Error);
        if (answer.Value.Status != 207)
            return CalDavResult<Multistatus>.Failure(Error(CalDavErrorCodes.InvalidResponse,
                $"The server answered {answer.Value.Status} instead of 207 Multi-Status.", answer.Value.Status, answer.Value.Url));
        // An empty 207 (python-caldav and dav4jvm read it as no responses) lists nothing.
        if (answer.Value.Body.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
            return CalDavResult<Multistatus>.Success(new Multistatus(new DavXmlElement(DavNames.D.Multistatus, [], ""), answer.Value.Url));
        // Servers' answers are read with characters XML forbids allowed: some pass raw control characters in calendar data
        // through, and one such object must not fail the whole step.
        var parsed = DavXml.Parse(WithoutControlBytes(answer.Value.Body),
            new DavXmlLimits(_options.MaxResponseBytes, _options.MaxXmlDepth, _options.MaxXmlElements), checkCharacters: false);
        if (parsed.Root is null)
            return CalDavResult<Multistatus>.Failure(parsed.Error == DavXmlErrorKind.TooLarge
                ? Error(CalDavErrorCodes.TooLarge, parsed.Message!, 207, answer.Value.Url) with { Limit = _options.MaxXmlElements }
                : Error(CalDavErrorCodes.InvalidResponse, parsed.Message!, 207, answer.Value.Url));
        if (!parsed.Root.Is(DavNames.D.Multistatus))
            return CalDavResult<Multistatus>.Failure(Error(CalDavErrorCodes.InvalidResponse, "The answer is not a DAV:multistatus.", 207, answer.Value.Url));
        return CalDavResult<Multistatus>.Success(new Multistatus(parsed.Root, answer.Value.Url));
    }

    // One request, following redirects with the same method, body and credentials; the body is read within the limit.
    private async Task<CalDavResult<Answer>> SendAsync(HttpMethod method, Uri url, HttpContent? content, string? depth, string? ifMatch,
        CancellationToken cancellationToken) {
        var body = content is null ? null : await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentType = content?.Headers.ContentType;
        content?.Dispose();
        using var timeout = _options.RequestTimeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout is { } limit) timeout!.CancelAfter(limit);
        var token = timeout?.Token ?? cancellationToken;
        try {
            for (var redirect = 0;; redirect++) {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = contentType;
                }

                if (depth is not null) request.Headers.TryAddWithoutValidation("Depth", depth);
                if (method == HttpMethod.Put) request.Headers.TryAddWithoutValidation(ifMatch is null ? "If-None-Match" : "If-Match", ifMatch ?? "*");
                else if (!string.IsNullOrEmpty(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                if (_options.Authorization is { } authorize) request.Headers.Authorization = await authorize(token).ConfigureAwait(false);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var isWrite = method == HttpMethod.Put || method == HttpMethod.Delete;
                // 303 to a write says it was done and where to look (RFC 9110 §15.4.4); repeating the PUT would be wrong.
                if (status == 303 && isWrite) return CalDavResult<Answer>.Success(new Answer(status, RawETag(response), [], url));
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location) {
                    if (redirect >= _options.MaxRedirects)
                        return CalDavResult<Answer>.Failure(Error(CalDavErrorCodes.InvalidResponse, $"More than {_options.MaxRedirects} redirects.", status, url));
                    var target = location.IsAbsoluteUri ? location : new Uri(url, location);
                    // Never from https to http: the credentials would go out in clear text (python-caldav, dav4jvm).
                    if (url.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
                        return CalDavResult<Answer>.Failure(Error(CalDavErrorCodes.InvalidResponse,
                            $"The server redirected from https to {target.Scheme}; the client does not follow it.", status, url));
                    url = target;
                    continue;
                }

                if (status >= 400) return CalDavResult<Answer>.Failure(StatusError(status, url));
                if (response.Content.Headers.ContentLength > _options.MaxResponseBytes) return CalDavResult<Answer>.Failure(TooLarge(url, status));
                var bytes = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
                if (bytes is null) return CalDavResult<Answer>.Failure(TooLarge(url, status));
                // A 207 to a write reports per resource (Xandikos, dav4jvm): any failed member fails the write.
                if (status == 207 && isWrite && FailedMemberStatus(bytes) is { } failed) return CalDavResult<Answer>.Failure(StatusError(failed, url));
                return CalDavResult<Answer>.Success(new Answer(status, RawETag(response), bytes, url));
            }
        }
        catch (OperationCanceledException) when (timeout is not null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException($"The CalDAV request to {url} took longer than {_options.RequestTimeout}.");
        }
    }

    private async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken) {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true) {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > _options.MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private CalDavError TooLarge(Uri url, int status) =>
        Error(CalDavErrorCodes.TooLarge, $"The answer is larger than {_options.MaxResponseBytes} bytes.", status, url) with { Limit = _options.MaxResponseBytes };

    private static CalDavError StatusError(int status, Uri url) => status switch {
        401 => Error(CalDavErrorCodes.Unauthorized, "The server refused the credentials.", status, url),
        403 => Error(CalDavErrorCodes.Forbidden, "The server refused the request.", status, url),
        404 or 410 => Error(CalDavErrorCodes.NotFound, "The resource does not exist.", status, url),
        // 409 is how SOGo and Radicale refuse a write that conflicts with another object (no-uid-conflict).
        409 or 412 => Error(CalDavErrorCodes.Conflict, "The object was changed on the server meanwhile, or conflicts with another.", status, url),
        _ => Error(CalDavErrorCodes.ServerError, $"The server answered {status}.", status, url),
    };

    private static CalDavError Error(string code, string message, int? status, Uri? url) =>
        new() { Code = code, Message = message, StatusCode = status, Url = url };

    private static Uri MemberUrl(CalDavCalendar calendar, string name) => new(WithSlash(calendar.Url), Uri.EscapeDataString(name));

    private static Uri WithSlash(Uri url) => url.AbsolutePath.EndsWith('/') ? url : new Uri(url + "/");

    // Compared decoded: a server may spell the collection "%40" in one place and "@" in another.
    private static bool IsSelf(CalDavCalendar calendar, Uri url) =>
        string.Equals(Uri.UnescapeDataString(WithSlash(url).AbsolutePath), Uri.UnescapeDataString(WithSlash(calendar.Url).AbsolutePath), StringComparison.Ordinal);

    // C0 control characters other than tab and line ends are one byte in UTF-8 and never part of a multi-byte sequence, so
    // they can be dropped from the bytes; the XML reader refuses them raw even when it does not check references.
    private static byte[] WithoutControlBytes(byte[] body) {
        if (body.AsSpan().IndexOfAnyInRange((byte)0, (byte)0x1F) < 0) return body;
        var kept = new byte[body.Length];
        var count = 0;
        foreach (var b in body)
            if (b >= 0x20 || b is (byte)'\t' or (byte)'\n' or (byte)'\r')
                kept[count++] = b;
        return count == body.Length ? body : kept[..count];
    }

    // The ETag header as sent; HttpClient's typed header is null for the unquoted or percent-encoded ones some servers send.
    private static string? RawETag(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault()?.Trim() : null;

    private int? FailedMemberStatus(byte[] body) {
        var parsed = DavXml.Parse(body, new DavXmlLimits(_options.MaxResponseBytes, _options.MaxXmlDepth, _options.MaxXmlElements), checkCharacters: false);
        if (parsed.Root is null || !parsed.Root.Is(DavNames.D.Multistatus)) return null;
        foreach (var response in parsed.Root.ChildrenNamed(DavNames.D.Response)) {
            var status = MultistatusResponse.ParseStatus(response.Child(DavNames.D.Status)?.Text)
                         ?? response.ChildrenNamed(DavNames.D.Propstat).Select(static p => MultistatusResponse.ParseStatus(p.Child(DavNames.D.Status)?.Text))
                             .FirstOrDefault(static s => s >= 400);
            if (status >= 400) return status;
        }

        return null;
    }

    private static string? NameOf(Uri url) {
        var path = url.AbsolutePath;
        if (path.EndsWith('/')) return null;
        var segment = path[(path.LastIndexOf('/') + 1)..];
        return segment.Length == 0 ? null : Uri.UnescapeDataString(segment);
    }

    // A weak tag cannot be used in If-Match; a bare one (Robur) is quoted, a percent-encoded one (Bedework 5) decoded.
    private static string? StrongETag(string? etag) {
        if (string.IsNullOrEmpty(etag) || etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) return null;
        if (etag.StartsWith("%22", StringComparison.OrdinalIgnoreCase)) etag = Uri.UnescapeDataString(etag);
        return etag.StartsWith('"') && etag.EndsWith('"') && etag.Length >= 2 ? etag : "\"" + etag.Trim('"') + "\"";
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>A parsed <c>multistatus</c>, its responses resolved against the URL that answered.</summary>
    private sealed class Multistatus(DavXmlElement root, Uri baseUrl) {
        public DavXmlElement Root { get; } = root;

        public Uri BaseUrl { get; } = baseUrl;

        public IEnumerable<MultistatusResponse> Responses =>
            Root.ChildrenNamed(DavNames.D.Response).Select(r => new MultistatusResponse(r, BaseUrl)).Where(static r => r.IsValid);
    }

    private sealed class MultistatusResponse {
        private readonly DavXmlElement _element;
        private readonly Uri _base;

        public MultistatusResponse(DavXmlElement element, Uri baseUrl) {
            _element = element;
            _base = baseUrl;
            var href = element.Child(DavNames.D.Href)?.Text.Trim();
            var resolved = href is { Length: > 0 } ? Resolve(baseUrl, href) : null;
            IsValid = resolved is not null;
            Url = resolved ?? baseUrl;
            Status = ParseStatus(element.Child(DavNames.D.Status)?.Text);
        }

        public bool IsValid { get; }

        public Uri Url { get; }

        public int? Status { get; }

        public bool HasOkPropstat => _element.ChildrenNamed(DavNames.D.Propstat).Any(static p => IsOk(p));

        public DavXmlElement? Prop(DavName name) {
            foreach (var propstat in _element.ChildrenNamed(DavNames.D.Propstat)) {
                if (!IsOk(propstat)) continue;
                if (propstat.Child(DavNames.D.Prop)?.Child(name) is { } found) return found;
            }

            return null;
        }

        public string? Text(DavName name) => Prop(name)?.Text.Trim() is { Length: > 0 } text ? text : null;

        public Uri? Href(DavName name) =>
            Prop(name)?.Child(DavNames.D.Href)?.Text.Trim() is { Length: > 0 } href ? Resolve(_base, href) : null;

        // A propstat without a status is read as 200 (dav4jvm, vdirsyncer: servers omit it); one with a status that does
        // not parse is not.
        private static bool IsOk(DavXmlElement propstat) =>
            propstat.Child(DavNames.D.Status) is not { } status || ParseStatus(status.Text) is >= 200 and < 300;

        /// <summary>
        /// An href resolved against the answering URL: an absolute http(s) URL, or a path. Anything else with a colon, such
        /// as "urn:uuid:4fbe.ics", is a relative path segment, not a URI of another scheme (dav4jvm).
        /// </summary>
        public static Uri? Resolve(Uri baseUrl, string href) {
            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : null;
            var relative = href.StartsWith('/') ? href : "./" + href;
            return Uri.TryCreate(baseUrl, relative, out var resolved) ? resolved : null;
        }

        internal static int? ParseStatus(string? line) {
            if (line is null) return null;
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : null;
        }
    }
}
