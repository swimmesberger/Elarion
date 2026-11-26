---
type: Research note
title: "CalDAV: what clients require, and what .NET offers"
description: "What iOS, macOS, Thunderbird, DAVx5, Evolution and Outlook require of a CalDAV server, the quirks of the hosted servers a CalDAV client meets, and which .NET libraries fit a NativeAOT framework package; input for ADR-0111."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T17:00:00Z }
status: draft
sources:
  - https://www.rfc-editor.org/rfc/rfc4918.html
  - https://www.rfc-editor.org/rfc/rfc4791.html
  - https://www.rfc-editor.org/rfc/rfc6578.html
  - https://www.rfc-editor.org/rfc/rfc6764.html
  - https://www.rfc-editor.org/rfc/rfc5397.html
  - https://www.rfc-editor.org/rfc/rfc6638.html
  - https://www.rfc-editor.org/rfc/rfc7809.html
  - https://github.com/mozilla/releases-comm-central
  - https://github.com/bitfireAT/davx5-ose
  - https://github.com/bitfireAT/dav4jvm
  - https://gitlab.gnome.org/GNOME/evolution-data-server
  - https://github.com/sabre-io/dav
  - https://github.com/Kozea/Radicale
  - https://github.com/emersion/go-webdav
  - https://github.com/shinyorg/aspnet
  - https://github.com/natelindev/tsdav
  - https://github.com/python-caldav/caldav
  - https://developers.google.com/calendar/caldav/v2/guide
  - https://learn.microsoft.com/en-us/graph/delta-query-events
---
# CalDAV: what clients require, and what .NET offers

- Date: 2026-11-26
- Status: Research input for [ADR-0111](../decisions/0111-caldav.md). This is not a decision.
- Question: what must a CalDAV server send so that iOS, macOS, Thunderbird and DAVx5 accept a read-only calendar,
  what does a CalDAV client meet on the hosted servers, and is there a .NET library a NativeAOT, dependency-light
  framework package can build on?
- Method: the open-source clients were read in their source on 2026-11-26, at the commits the links pin: Thunderbird
  (`releases-comm-central`), DAVx5 (`davx5-ose`) with `dav4jvm`, and evolution-data-server. Apple and Microsoft
  clients are closed, so their claims rest on vendor documentation, the sabre/dav client notes, forum threads and
  server bug trackers; they are weaker. .NET packages: the NuGet registration and search APIs and the GitHub API on
  the same day, and shallow clones searched with grep for `XmlSerializer`, `Activator.CreateInstance`,
  `MakeGenericType`, attribute scans and `IsAotCompatible`. A grep hit is evidence; a miss only means the pattern was
  not found. Nothing was compiled or published with NativeAOT for this note. **unverified** marks a claim no primary
  source confirmed; **absence** marks a feature searched for in a client's source and not found, which is weaker
  than finding it. **Inference** marks my reading.

## Summary

Hard requirements for a read-only server that works on iOS, macOS, Thunderbird and DAVx5:

1. **HTTPS.** Apple clients may refuse to send credentials over plain HTTP
   ([Radicale docs](https://radicale.org/v3.html), [sabre iCal notes](https://sabre.io/dav/clients/ical),
   [Apple forum 691818](https://developer.apple.com/forums/thread/691818)).
2. **An anonymous request gets 401 with `WWW-Authenticate: Basic realm=…`, never 403.** iOS sends anonymous requests
   during discovery, and iOS 15 stopped working against a server that answered them with 403
   ([Apple forum 691818](https://developer.apple.com/forums/thread/691818)).
3. **`/.well-known/caldav` redirects (301, 303 or 307) to the context path, and the redirect answers PROPFIND.** iOS,
   Thunderbird and DAVx5 send PROPFIND there; a 307 works on iOS 26.6
   ([RFC 6764 §5](https://www.rfc-editor.org/rfc/rfc6764.html#section-5),
   [Stalwart iOS 26.6 trace](https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405),
   [Thunderbird L244-253](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavProvider.sys.mjs#L244-L253),
   [DAVx5 L120-187](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L120-L187)).
4. **PROPFIND Depth 0 on the context path and on every calendar URL returns `DAV:current-user-principal`**
   ([RFC 6764 §6](https://www.rfc-editor.org/rfc/rfc6764.html#section-6),
   [RFC 5397 §3](https://www.rfc-editor.org/rfc/rfc5397.html#section-3)).
5. **OPTIONS on the principal answers 200 with `DAV: 1, 3, calendar-access`.** DAVx5 drops a principal without
   `calendar-access` ([DAVx5 L336-359](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L336-L359));
   Thunderbird sends OPTIONS to the calendar's parent URL and needs a 200
   ([Thunderbird L1753-1820](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1753-L1820)).
6. **The principal answers `C:calendar-home-set`, `C:calendar-user-address-set`, `D:displayname` and
   `D:principal-URL`** ([sabre iOS notes](https://sabre.io/dav/clients/ios),
   [Thunderbird L1905-1970](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1905-L1970)).
7. **Every calendar has `resourcetype` `<D:collection/><C:calendar/>`.** Thunderbird disables anything else as
   "not CalDAV" ([Thunderbird L1574-1740](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1574-L1740)).
8. **Read-only is `D:current-user-privilege-set` with `D:read` and none of `D:write`, `D:write-content`, `D:bind`,
   `D:all`, and not `D:write-properties`.** A missing property means writable in Thunderbird, DAVx5 and Evolution
   ([Bugzilla 1738443](https://bugzilla.mozilla.org/show_bug.cgi?id=1738443),
   [DAVx5 Collection L170-273](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/db/Collection.kt#L170-L273)).
9. **Every object answers a strong, quoted `D:getetag` and a `D:getcontenttype` starting with `text/calendar`.**
   Thunderbird silently skips members without either
   ([Thunderbird handlers L340-376](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/modules/CalDavRequestHandlers.sys.mjs#L340-L376)).
10. **`sync-collection` (RFC 6578), listed in `D:supported-report-set`, with a sync token that is a URI** (contains
    `:` or `/`) and deleted members as 404 responses
    ([RFC 6578 §3.2](https://www.rfc-editor.org/rfc/rfc6578.html#section-3.2),
    [EDS L1100-1210](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L1100-L1210)).
11. **An invalid or expired token gets 403 with `<D:error><D:valid-sync-token/></D:error>`** (see the first
    surprise below).
12. **`CS:getctag` on each calendar.** Deprecated, but Thunderbird and Evolution use it without sync-collection and
    older Apple clients rely on it; Apple's spec allows the sync-token value as its value
    ([CalendarServer ctag spec](https://github.com/apple/ccs-calendarserver/blob/master/doc/Extensions/caldav-ctag.txt)).
13. **`calendar-multiget` returning `getetag` and `calendar-data`, and `calendar-query` with a VEVENT `comp-filter`
    and `time-range`** that matches a recurring master by any instance in the range
    ([RFC 4791 §7.8](https://www.rfc-editor.org/rfc/rfc4791.html#section-7.8),
    [§9.9](https://www.rfc-editor.org/rfc/rfc4791.html#section-9.9), [sabre iOS notes](https://sabre.io/dav/clients/ios)).
14. **A write to a read-only calendar answers 403 with `DAV:need-privileges`, and every TZID has a VTIMEZONE (or times
    are UTC).** DAVx5 then discards the local change
    ([DAVx5 SyncManager L380-445](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/sync/SyncManager.kt#L380-L445),
    [RFC 4791 §4.1](https://www.rfc-editor.org/rfc/rfc4791.html#section-4.1)).

Surprises:

- **Clients disagree on the invalid-token status.** RFC 6578 defines the `valid-sync-token` precondition but no
  status. DAVx5 starts over only when the element is present
  ([SyncManager L674-720](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/sync/SyncManager.kt#L674-L720)).
  Evolution starts over on 412, or on 403 that names `valid-sync-token`
  ([EDS L1100-1210](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L1100-L1210)).
  Thunderbird starts over on any 4xx except 429 and a 403 with an empty `<D:error/>`, which it reads as rate
  limiting ([handlers L533-564](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/modules/CalDavRequestHandlers.sys.mjs#L533-L564)).
  Servers differ too: sabre/dav 403, Xandikos 412, CCS 500
  ([python-caldav hints](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L880-L925)).
  403 with the element satisfies all three clients.
- **Thunderbird before 154 counted `D:write-properties` as write access**, so a read-only share that granted it
  looked writable and writes failed ([Bugzilla 2051561](https://bugzilla.mozilla.org/show_bug.cgi?id=2051561)).
- **Apple push is out of reach.** The `push-transports` topic comes from a certificate Apple issues
  ([CalendarServer pubsub spec](https://github.com/apple/ccs-calendarserver/blob/master/doc/Extensions/caldav-pubsubdiscovery.txt));
  whether a third party can obtain one today is **unverified**. Expect iOS and macOS to poll.
- **iOS 26.6 account setup.** Adding the account through Mail settings stalls after the principal OPTIONS; Settings →
  Apps → Calendar → Calendar Accounts → Add Account → Other → Add CalDAV Account works
  ([Stalwart forum](https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405)).
- **Outlook and Google Calendar cannot use a generic CalDAV server.** Outlook for Mac has no CalDAV
  ([Microsoft support](https://support.microsoft.com/en-gb/office/sync-your-icloud-calendar-with-outlook-for-mac-c9c67e41-274f-4527-ae5e-ea1003d89fc5)),
  classic Windows Outlook needs the CalDAV Synchronizer add-in ([caldavsynchronizer.org](https://caldavsynchronizer.org/)),
  and Google Calendar only subscribes to a public URL ([Google help 37100](https://support.google.com/calendar/answer/37100?hl=en)).
  For them the server needs an ICS subscription feed next to CalDAV.

## Clients as consumers of a server

### Discovery

| Client | User enters | Discovery as implemented or observed |
| --- | --- | --- |
| iOS | Server, user name, password ([Stalwart forum](https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405)); MDM payload `CalDAVHostName`, `CalDAVPrincipalURL`, `CalDAVUseSSL` (default true) ([Apple payload](https://developer.apple.com/documentation/devicemanagement/caldav)) | iOS 26.6: PROPFIND well-known → 307 → PROPFIND context → `current-user-principal` → OPTIONS principal → PROPFIND home ([Stalwart](https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405)). Older iOS needed the server at the domain root or a full principal URL ([sabre iOS](https://sabre.io/dav/clients/ios)). Whether it rejects a principal without `calendar-access` is **unverified** |
| macOS | User name, password, server, optional path | 10.6+ discovers; with an e-mail address it queries well-known on the mail domain ([sabre service discovery](https://sabre.io/dav/service-discovery/)); the principal URL needs a trailing slash that the response `href` keeps ([sabre iCal](https://sabre.io/dav/clients/ical)) |
| Thunderbird 102+ | E-mail and an optional location | Google OAuth (Google domain or Google MX) → the location → SRV `_caldavs._tcp`/`_caldav._tcp` + TXT path → well-known → `/`; each probe is a PROPFIND that follows a calendar, home set or principal ([L44-76](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavProvider.sys.mjs#L44-L76), [L301-350](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavProvider.sys.mjs#L301-L350)). No OPTIONS during detection |
| DAVx5 | E-mail, or base URL + user name; OAuth for Google and Fastmail; Nextcloud Login Flow; client certificates | PROPFIND the URL → well-known on the same host → for https only, SRV `_caldavs._tcp` + TXT → well-known → `/` ([L120-187](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L120-L187), [L361-425](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L361-L425)). Requires `calendar-access` in the principal's OPTIONS |
| Evolution | A CalDAV URL, or a GNOME Online Accounts CalDAV account | PROPFIND for `current-user-principal` and `calendar-home-set`; retries at well-known when nothing is found ([e-webdav-discover L985-1010](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/libedataserver/e-webdav-discover.c#L985-L1010)). SRV **unverified** |

Protocol points:

- RFC 6764: SRV `_caldavs._tcp` before `_caldav._tcp`, TXT `path=` for the context path; without SRV the client
  "MUST first attempt an HTTP connection with TLS" ([RFC 6764](https://www.rfc-editor.org/rfc/rfc6764.html)).
  Well-known MUST NOT serve CalDAV itself, MAY require authentication first and SHOULD send `Cache-Control`
  ([§5](https://www.rfc-editor.org/rfc/rfc6764.html#section-5)).
- The `Location` must be `https://`; an `http://` Location broke Apple profile installs
  ([NethServer thread](https://community.nethserver.org/t/ns8-well-known-redirections-for-nextcloud-or-other-apps/27888)).
- A server MUST put `calendar-access` in the `DAV:` header of every CalDAV resource
  ([RFC 4791 §5.1](https://www.rfc-editor.org/rfc/rfc4791.html#section-5.1)). sabre/dav also sends `DAV:` on PROPFIND
  responses because macOS needs it there for calendar-proxy ([sabre iCal](https://sabre.io/dav/clients/ical)).
- Without `current-user-principal`, DAVx5 and Thunderbird still accept a URL that is itself a calendar or home set;
  Apple clients need it or a hand-entered principal URL
  ([DAVx5 L281-334](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L281-L334),
  [RFC 6764 §6](https://www.rfc-editor.org/rfc/rfc6764.html#section-6)).
- Without `calendar-user-address-set`, DAVx5 asks the user for the account e-mail
  ([L244-265](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt#L244-L265)).

### Properties

| Property | Apple (iOS, macOS) | Thunderbird | DAVx5 | Evolution |
| --- | --- | --- | --- | --- |
| `D:resourcetype` | requested | **mandatory** `C:calendar` | **mandatory** | requested |
| `D:displayname` | requested | falls back to the last URL segment | requested | requested |
| `CS:getctag` | requested | only without sync-collection | read with the sync token | assumes support until it fails |
| `D:sync-token` | macOS 10.9 requests it; current iOS **unverified** | from the sync REPORT | requested | requested |
| `D:supported-report-set` | requested | **decides** sync-collection | **decides** sync-collection | **decides** sync-collection |
| `C:supported-calendar-component-set` | requested | optional filter | requested | — |
| `A:calendar-color` | requested | strips the alpha of `#RRGGBBAA` | requested | — |
| `D:current-user-privilege-set` | requested on the home | read-only detection | read-only detection | read-only detection |
| `D:getetag` | in query and multiget | **mandatory** per member | requested | requested |
| `D:getcontenttype` | — | **mandatory**, `text/calendar…` | — | — |
| `C:calendar-timezone` | macOS 10.6 | — | used as the Android calendar's zone | — |
| `CS:push-transports`, `CS:pushkey` | requested | — | WebDAV-Push properties instead | — |

Sources: [sabre iOS](https://sabre.io/dav/clients/ios), [sabre iCal](https://sabre.io/dav/clients/ical),
[Thunderbird L1574-1740](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1574-L1740),
[Thunderbird provider L399-466](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavProvider.sys.mjs#L399-L466),
[DAVx5 Collection](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/db/Collection.kt#L170-L273),
[DAVx5 manual](https://manual.davx5.com/technical_information.html),
[EDS L1100-1210](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L1100-L1210).
An unknown property comes back in a 404 propstat, a forbidden one in a 403 propstat
([sabre client guide](https://sabre.io/dav/building-a-caldav-client/)).

### Sync

| Client | Algorithm | Polling | Push |
| --- | --- | --- | --- |
| iOS | Historically ctag on the home → `calendar-query` with a time range for ETags → multiget ([sabre iOS](https://sabre.io/dav/clients/ios)); sync-collection use **unverified** | Fetch intervals from third-party pages only (**unverified**) | APNs; third-party availability **unverified** |
| macOS | Requests `sync-token`, `getctag`, `push-transports` (10.9); algorithm **unverified** | Per-account refresh setting ([Apple](https://support.apple.com/guide/calendar/refresh-calendars-icl1024/mac)) | APNs |
| Thunderbird | sync-collection (level 1, `getcontenttype` + `getetag`) when advertised; else ctag → PROPFIND Depth 1 → multiget; continues after a 507 ([handlers L402-460](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/modules/CalDavRequestHandlers.sys.mjs#L402-L460)) | 30 min default ([CalCalendarManager](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/base/src/CalCalendarManager.sys.mjs#L480-L500)) | none |
| DAVx5 | sync-collection when advertised and no past-days limit; else listing + multiget in batches of 10; follows 507 ([CalendarSyncManager L88-92](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/sync/CalendarSyncManager.kt#L88-L92), [manual](https://manual.davx5.com/technical_information.html)) | 4 h default ([DefaultsProvider](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/settings/DefaultsProvider.kt#L26)) | WebDAV-Push ([repo](https://github.com/bitfireAT/webdav-push)) |
| Evolution | ctag, then sync-collection when advertised and no download-days limit, else `calendar-query` + multiget ([EDS L1000-1031](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L1000-L1031)) | **unverified** | none |

RFC 6578: the token is "Any valid URI" ([§4](https://www.rfc-editor.org/rfc/rfc6578.html#section-4)); an initial sync
sends an empty token and MUST NOT list removed members; removed members are a 404 response without propstat;
truncation is a 507 on the request-URI; servers SHOULD invalidate tokens only "when absolutely necessary"
([§3](https://www.rfc-editor.org/rfc/rfc6578.html#section-3.2)). Evolution treats a token with neither `:` nor `/`
as a stale ctag and does a full sync every time
([EDS L1100-1210](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L1100-L1210)).
WebDAV-Push (draft) advertises `webdav-push` in `DAV:`, subscribes with `POST` + `push-register`, notifies through
Web Push, and says clients must still poll ([draft](https://bitfireat.github.io/webdav-push/draft-bitfire-webdav-push-00.html)).

### Read-only detection

| Client | Detection | Effect |
| --- | --- | --- |
| Thunderbird | Read-only unless the privilege set holds `write`, `write-content`, `bind` or `all`; a 404 for the property leaves it writable ([L1574-1740](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1574-L1740), [Bugzilla 1738443](https://bugzilla.mozilla.org/show_bug.cgi?id=1738443)) | Calendar flagged read-only; UI greying **unverified** |
| DAVx5 | `write-content` and `unbind` from the privilege set; `write` and `all` imply both; absent → writable ([dav4jvm](https://github.com/bitfireAT/dav4jvm/blob/493335bb6c00e38fffe7f14f09e126ecfa608446/src/main/kotlin/at/bitfire/dav4jvm/property/webdav/CurrentUserPrivilegeSet.kt#L20-L70)) | Android calendar gets `CAL_ACCESS_READ` ([LocalCalendarStore](https://github.com/bitfireAT/davx5-ose/blob/684d863a78237f0f2de9adfbb234c3786e0dec8a/core/src/main/kotlin/at/bitfire/davdroid/resource/local/LocalCalendarStore.kt#L170-L195)); a 403 on upload discards the local change |
| Evolution | Writable on `write`, `write-content` or `all`; if the privilege request fails, writable when `Allow` lists PUT, POST or DELETE ([EDS L270-330](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L270-L330)) | Read-only in Evolution. A read-only server should not list those methods in `Allow` |
| iOS, macOS | Request the privilege set on the home ([sabre iOS](https://sabre.io/dav/clients/ios)) | Current UI behaviour **unverified** |
| ICS subscriptions | Always read-only ([Apple iPhone guide](https://support.apple.com/guide/iphone/use-multiple-calendars-iph3d1110d4/ios)) | — |

### Authentication

| Client | Schemes | Preemptive | Notes |
| --- | --- | --- | --- |
| iOS | Basic (Digest **unverified**) | No; needs the 401 challenge ([Apple forum 691818](https://developer.apple.com/forums/thread/691818)) | Sends URL-encoded user names ([Radicale docs](https://radicale.org/v3.html)) |
| macOS | Basic | **unverified** | May send no credentials over HTTP; a `%` in the user name made 10.11 send none ([sabre iCal](https://sabre.io/dav/clients/ical)) |
| Thunderbird | Basic and Digest after the challenge; OAuth2 for Google and Fastmail ([CalDavSession](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/modules/CalDavSession.sys.mjs#L55-L92)) | Challenge-driven (**unverified** in this source) | Asks before sending credentials to an SRV target on another site |
| DAVx5 | Basic preemptively, Digest when challenged ([dav4jvm](https://github.com/bitfireAT/dav4jvm/blob/493335bb6c00e38fffe7f14f09e126ecfa608446/src/main/kotlin/at/bitfire/dav4jvm/ktor/PreemptiveBasicDigestAuthProvider.kt)); OAuth for Google and Fastmail; client certificates | Yes | Credentials bound to the account's domain |
| Evolution | **unverified** | **unverified** | Special-cases Google hosts and iCloud |

App-password precedents: iCloud (two-factor required, up to 25, revoked on password change,
[Apple 102654](https://support.apple.com/en-us/102654)); Fastmail (16 characters, about 80 bits, scoped, last use
shown, [Fastmail](https://www.fastmail.help/hc/en-us/articles/360058752854-App-passwords)); Nextcloud (per device,
shown once, revocable, [Nextcloud](https://docs.nextcloud.com/server/latest/user_manual/en/session_management.html)).

### ETags, time zones, recurrence, scheduling

- **ETags.** A CalDAV server MUST support ETags, and MUST NOT return a strong ETag for a PUT whose data it changed
  ([RFC 4791 §5.3.4](https://www.rfc-editor.org/rfc/rfc4791.html#section-5.3.4)). Thunderbird and DAVx5 create with
  `If-None-Match: *` and update or delete with `If-Match`
  ([Thunderbird CalDavRequest](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/modules/CalDavRequest.sys.mjs#L650-L715),
  [DAVx5 manual](https://manual.davx5.com/technical_information.html)). DAVx5 resets its copy when a PUT returns no
  ETag. No Schedule-Tag handling in Thunderbird (**absence**).
- **Time zones.** Every TZID in an object needs a VTIMEZONE ([RFC 4791 §4.1](https://www.rfc-editor.org/rfc/rfc4791.html#section-4.1)).
  RFC 7809 (time zones by reference) is opt-in; no client implementing it was found (**unverified**)
  ([RFC 7809](https://www.rfc-editor.org/rfc/rfc7809.html)). DAVx5 maps Windows zone names and guesses unknown ones
  ([manual](https://manual.davx5.com/technical_information.html)). Safe output: IANA TZIDs with VTIMEZONE, or UTC; no
  floating times.
- **Recurrence.** The server MUST support `expand` and `limit-recurrence-set`
  ([RFC 4791 §9.6.5](https://www.rfc-editor.org/rfc/rfc4791.html#section-9.6.5)), but Thunderbird, DAVx5 and
  Evolution never send them (**absence**; Apple **unverified**). Time-range filters, which iOS and Evolution use, must
  match recurring masters by instance ([§9.9](https://www.rfc-editor.org/rfc/rfc4791.html#section-9.9)).
- **Free-busy.** Evolution sends `free-busy-query`
  ([EDS L2565-2600](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L2565-L2600));
  Thunderbird uses it only with scheduling, through the RFC 6638 outbox
  ([L1753-1820](https://github.com/mozilla/releases-comm-central/blob/6f71a14ff4d348595373eed977a7a0edc94c593f/calendar/providers/caldav/CalDavCalendar.sys.mjs#L1753-L1820)).
- **Scheduling.** Advertised by `calendar-auto-schedule` in `DAV:` ([RFC 6638 §2](https://www.rfc-editor.org/rfc/rfc6638.html)).
  Without it or `calendar-schedule`, Thunderbird skips principal, outbox and free-busy discovery. Evolution assumes
  scheduling when OPTIONS returns no capabilities at all
  ([EDS L270-330](https://gitlab.gnome.org/GNOME/evolution-data-server/-/blob/55d9d5a43988565a5b5d86897aa4d7f5adfcdbe0/src/calendar/backends/caldav/e-cal-backend-caldav.c#L270-L330)).
  DAVx5 sends no invitations ([manual](https://manual.davx5.com/technical_information.html)). A read-only server
  advertises neither token and omits the inbox and outbox URLs.

### Outlook and Google Calendar

- Classic Windows Outlook: CalDAV only through the CalDAV Synchronizer add-in, v4.7.1 of 2026-08-17
  ([GitHub](https://github.com/aluxnimm/outlookcaldavsynchronizer)). New Outlook for Windows has no COM add-ins, so no
  CalDAV (Microsoft Q&A answer, **unverified**, [link](https://learn.microsoft.com/en-us/answers/a/2059193)). Outlook
  for Mac has none ([Microsoft support](https://support.microsoft.com/en-gb/office/sync-your-icloud-calendar-with-outlook-for-mac-c9c67e41-274f-4527-ae5e-ea1003d89fc5)).
- ICS subscriptions refresh about every 3 hours in Outlook.com and every 6 in Outlook on the web, and "may take more
  than 24 hours" ([Microsoft support](https://support.microsoft.com/en-us/office/import-or-subscribe-to-a-calendar-in-outlook-com-or-outlook-on-the-web-cff1429c-5af6-41ec-a5b4-74f2c278e98c)).
- Google Calendar subscribes "From URL" on the web only, for public calendars
  ([Google help 37100](https://support.google.com/calendar/answer/37100?hl=en)); the refresh interval is undocumented
  (12-24 h by third-party report, **unverified**, [Simon Willison](https://simonwillison.net/2022/Jul/30/gas-ics-sync/)).
  On Android, the Google Calendar app shows DAVx5 calendars
  ([DAVx5 FAQ](https://www.davx5.com/faq/calendars/google-calendar-can-i-use-davx5-with-google-calendar)).

## Servers as targets of a client

| Server | Discovery | Auth | Sync | Quirks |
| --- | --- | --- | --- | --- |
| Google (CalDAV v2) | Principal `https://apidata.googleusercontent.com/caldav/v2/CALENDAR_ID/user`, collection `…/events` | **OAuth 2.0 only**; Basic gets 401; needs a Cloud project with the CalDAV API on | RFC 6578 required after the initial sync; ctag | No MKCALENDAR, COPY, MOVE; only `If-Match`; no ACL, so no privilege set; no free-busy-query; no VTODO ([guide](https://developers.google.com/calendar/caldav/v2/guide)) |
| iCloud | `https://caldav.icloud.com/`; home sets on partition hosts | App-specific password | sync-token; `DAV:limit` ignored | Depth-1 PROPFIND on the principal → 400; UID filter → 412; no Schedule-Tag; `If-Match: *` inverted ([python-caldav hints](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L2510-L2565)) |
| Fastmail | `https://caldav.fastmail.com/`, TLS ([Fastmail](https://www.fastmail.help/hc/en-us/articles/1500000278342-Server-names-and-ports)) | App password or OAuth; the account password does not work | **unverified** | Only a legacy flag list in python-caldav |
| Nextcloud | `/remote.php/dav`, 301 well-known | Device passwords, Login Flow | `DAV:limit` cut without 507; a deleted and re-created member reported as removed ([hints](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L1815-L1940)) | Trash bin blocks reuse of a calendar id |
| Yahoo | `https://caldav.calendar.yahoo.com/`; "seemingly discontinued" per [DAVx5](https://www.davx5.com/tested-with) | 401 **without `WWW-Authenticate`**; fall back to Basic | **No sync-token** | `comp-filter` ignored; events with a time zone cannot be read back; `If-Match: *` inverted ([hints](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L3570-L3680)) |
| Zimbra | `/dav/` | **unverified** | "fragile" | PUT under a name other than `<uid>.ics` redirected; multiget returns deleted objects; RDATE dropped ([hints](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L1938-L2100)) |
| Microsoft 365 | No CalDAV endpoint documented (**absence**) | Entra ID OAuth | Graph `GET /me/calendarView/delta?startDateTime=…&endDateTime=…`: `@odata.nextLink`, then `@odata.deltaLink`; opaque tokens that encode the range; deletions as `@removed`; delta without a range is beta only ([Graph](https://learn.microsoft.com/en-us/graph/delta-query-events)) | Track each calendar separately |

Conditional requests are unreliable across servers: Zimbra and Radicale before 3.9.0 compare `*` as a literal ETag,
SOGo ignores `If-None-Match`, Zimbra, Bedework and Posteo ignore a stale `If-Match` on DELETE, and several servers
rewrite data yet return a strong ETag
([hints L1005-1095](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L1005-L1095)).
A client resolves hrefs against the URL that answered, uses sync-collection only when advertised, does not rely on
507, filters the collection's own href from sync results, falls back to a full sync on any `valid-sync-token`
error, and re-GETs after a PUT with a weak or missing ETag
([hints L880-925](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L880-L925)).

## .NET libraries

| Library | Role | License | Latest release | AOT status | CalDAV coverage | Fit |
| --- | --- | --- | --- | --- | --- | --- |
| [Shiny.AspNet.CalDav](https://www.nuget.org/packages/Shiny.AspNet.CalDav/1.0.0) 1.0.0 | server | MIT | 2026-10-12, 209 downloads, repo 1 star | `IsAotCompatible=true`; `XmlReader`/`XmlWriter` only, no reflection found ([repo](https://github.com/shinyorg/aspnet)) | query, multiget, free-busy, sync-collection, MKCALENDAR, well-known, CardDAV; no scheduling | Reference, not dependency (below) |
| [IT Hit WebDAV Server Engine](https://www.nuget.org/packages/ITHit.WebDAV.Server/15.5.16192) 15.5 | server | commercial | 2026-11-25 | **unverified** | CalDAV, CardDAV; RFC 6578 **unverified** ([product page](https://www.webdavsystem.com/server/)) | Licence rules it out |
| [NWebDav](https://github.com/ramondeklein/nwebdav) 0.1.36 / 0.2.0-beta.2 | server | MIT | 2021 / 2024; archived | attribute reflection | none | Dead, no CalDAV |
| [FubarDev.WebDavServer](https://github.com/FubarDevelopment/WebDavServer) 1.0.2 | server | MIT | 2019 | `XmlSerializer` | none | Stale, blocks AOT |
| [WebDAVSharp.Server](https://github.com/WebDAVSharp/WebDAVSharp.Server) 1.0.7 | server | MIT | 2014 | `XmlDocument`, `HttpListener` | none | Dead |
| [closureOSS/calendare](https://github.com/closureOSS/calendare) | server application | MIT | push 2026-07-17 | EF Core | broad | No reusable seam; Postgres design reference |
| [WebDav.Client](https://github.com/skazantsev/WebDavClient) 2.9.0 | client | MIT | 2025-03-30 | none found, no flag | no `REPORT` | Cannot sync |
| [WebDAVClient](https://github.com/saguiitay/WebDAVClient) 3.3.0 | client | MIT | 2026-10-23 | none found, no flag | WebDAV + `REPORT`, no CalDAV model | File-oriented |
| [CalDAV.NET](https://github.com/markatk/CalDAV.NET) 0.1.0 | client | MIT | archived 2024 | via Ical.Net | basic | Dead |
| [SeppPenner CalDAVNet](https://github.com/SeppPenner/CalDAVNet) 1.1.0 | client | MIT | 2026-11-24 | via Ical.Net 5.2.4 | basic, no sync | Ical.Net |
| [Semisoft.CalDAVNet](https://github.com/samwizardry/CalDAVNet) 1.1.4 | client | MIT | 2026-11-09, 100 downloads | via Ical.Net 5.2.3 | sync token, multiget | Ical.Net, no users |
| [jvilalta.CalDAV](https://github.com/jvilalta/CalDAV) 2.3.0 | client | MIT | 2026-03-30 | via Ical.Net 4.2.0 | basic | Ical.Net |
| [AMWD.Protocols.CalDAV](https://github.com/AM-WD/AMWD.Protocols.DAV) 0.1.1 | client | MIT | 2026-09-28 | `XmlDocument` + Ical.Net 5.2.3 | basic, no sync | Ical.Net |
| [Ical.Net](https://github.com/ical-org/ical.net) 5.2.4 | iCalendar | MIT | 2026-10-31, 40.9 M downloads | `Activator.CreateInstance`, `MakeGenericType`, attribute scan; no AOT flag | n/a | Silently wrong under AOT (below) |

- No CalDAV client sets `IsAotCompatible`, and every maintained one depends on Ical.Net. None covers the full flow
  of discovery, principal, home set, Depth 1 listing, sync-collection with 507 and `valid-sync-token` handling,
  multiget, and conditional writes.
- Ical.Net's open PR #977 reports that under `PublishAot` with full trimming the library ran without error and
  "emitted a wrong calendar": the trimmed `[OnDeserializing]` callback leaves a random GUID as the parsed `UID`
  ([PR #977](https://github.com/ical-org/ical.net/pull/977)). Until it ships, any library on Ical.Net corrupts data
  under NativeAOT, per that PR.
- **Conclusion: no library fits a NativeAOT, dependency-light framework package.** Shiny.AspNet.CalDav is the only
  AOT-clean CalDAV server. It is a design reference and a possible test oracle, not a dependency: 1.0.0 shipped on
  2026-10-12 with almost no users; its sync tokens are hashes of listings whose snapshots live in memory, a ring of
  32 per collection, so a token is lost on restart and unknown on another node
  ([SyncTracker.cs](https://github.com/shinyorg/aspnet/blob/main/src/Shiny.AspNet.CalDav/Internal/SyncTracker.cs);
  the multi-node effect is an **inference**); it serialises writes per collection in process; and it has its own
  iCalendar parser, which would duplicate an Elarion iCalendar model.

## XML and ASP.NET Core under NativeAOT

- **`XmlSerializer` is not AOT-safe.** Its constructors carry `RequiresDynamicCode` and `RequiresUnreferencedCode`
  ([reference assembly](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Xml.XmlSerializer/ref/System.Xml.XmlSerializer.cs),
  [trimming incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)).
- **`XmlReader`, `XmlWriter`, `XmlDocument` and `XDocument` carry no such annotations**; in `System.Xml.ReaderWriter`
  only `XmlSchema` and XSLT do
  ([ReaderWriter ref](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Xml.ReaderWriter/ref/System.Xml.ReaderWriter.cs),
  [XDocument ref](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Xml.XDocument/ref/System.Xml.XDocument.cs)).
  **Inference:** they produce no trim or AOT warnings.
- **The `XDocument.Load` pitfall.** `XmlReaderSettings` disables DTD processing by default
  ([docs](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-xml-xmlreadersettings)), but
  `XDocument.Load(Stream)` and `XElement.Load(Stream)` build their own settings with `DtdProcessing.Parse` and
  `MaxCharactersFromEntities = 1e7`, so entity expansion up to 10 M characters is allowed
  ([XNode.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.Xml.Linq/src/System/Xml/Linq/XNode.cs)).
  Hardened reader: `XmlReader.Create(stream, settings)` with `DtdProcessing.Prohibit`, `XmlResolver = null`,
  `MaxCharactersFromEntities = 0`, a `MaxCharactersInDocument` cap and `Async = true`, plus a request-body cap as
  Shiny's `MaxXmlBodyBytes` (1 MiB) does ([DavXml.cs](https://github.com/shinyorg/aspnet/blob/main/src/Shiny.AspNet.CalDav/Internal/DavXml.cs)).
- **Custom verbs.** `MapMethods(pattern, methods, handler)` accepts PROPFIND, REPORT and the rest; Shiny maps them
  this way ([CalDavEndpoints.cs](https://github.com/shinyorg/aspnet/blob/main/src/Shiny.AspNet.CalDav/CalDavEndpoints.cs)),
  and the Request Delegate Generator intercepts `MapMethods`
  ([RDG source](https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http.Extensions/gen/Microsoft.AspNetCore.Http.RequestDelegateGenerator/StaticRouteHandlerModel/InvocationOperationExtensions.cs)).
  A plain `RequestDelegate` with manual XML parsing needs no generated binding (**unverified** inference).
  `HttpMethods` has no DAV verb constants.
- **`.well-known` at the application root.** It must be mapped on the root route builder, not inside a prefixed
  group ([CalDavEndpoints.cs](https://github.com/shinyorg/aspnet/blob/main/src/Shiny.AspNet.CalDav/CalDavEndpoints.cs),
  [RFC 6764 §5](https://www.rfc-editor.org/rfc/rfc6764#section-5)). go-webdav redirects with 308
  ([server.go](https://github.com/emersion/go-webdav/blob/master/caldav/server.go)).
- **CORS preflight versus DAV OPTIONS.** A preflight carries `Origin` and `Access-Control-Request-Method`; the CORS
  middleware short-circuits it with 204, and routing answers it only for endpoints with `AcceptCorsPreflight`
  ([HttpMethodMatcherPolicy](https://github.com/dotnet/aspnetcore/blob/main/src/Http/Routing/src/Matching/HttpMethodMatcherPolicy.cs),
  [CorsMiddleware](https://github.com/dotnet/aspnetcore/blob/main/src/Middleware/CORS/src/Infrastructure/CorsMiddleware.cs)).
  A plain DAV `OPTIONS` reaches an explicit `MapMethods(["OPTIONS"])` endpoint. Every DAV verb from a browser
  triggers a preflight ([CORS docs](https://learn.microsoft.com/en-us/aspnet/core/security/cors)).
- **No built-in Basic handler.** ASP.NET Core ships none
  ([Authentication folder](https://github.com/dotnet/aspnetcore/tree/main/src/Security/Authentication)); the usual
  packages are `idunno.Authentication.Basic` (a self-declared demonstration,
  [repo](https://github.com/blowdart/idunno.Authentication/tree/dev/src/idunno.Authentication.Basic)),
  `AspNetCore.Authentication.Basic` and `ZNetCS.AspNetCore.Authentication.Basic`. A custom
  `AuthenticationHandler<TOptions>` is about 50 lines; Microsoft's AOT table marks authentication other than JWT as
  not supported, and whether a custom handler warns is **unverified**
  ([native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot)).
- **Slim builder.** `CreateSlimBuilder` omits HTTPS endpoints and regex and alpha route constraints
  ([native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot)); a `{**path}` catch-all needs
  neither.
- DAV header ([RFC 4918 §10.1](https://www.rfc-editor.org/rfc/rfc4918#section-10.1)) and 207 Multi-Status
  ([§13](https://www.rfc-editor.org/rfc/rfc4918#section-13)); `Depth: infinity` on PROPFIND may be refused with 403
  `propfind-finite-depth` ([§9.1.1](https://www.rfc-editor.org/rfc/rfc4918#section-9.1.1)).

## Backend shapes of other servers

| Server | Addressing | List | Fetch | Query | Sync |
| --- | --- | --- | --- | --- | --- |
| [sabre/dav](https://github.com/sabre-io/dav/blob/master/lib/CalDAV/Backend/BackendInterface.php) (PHP) | principal URI, calendar id, object URI | `getCalendarObjects` without body | `getCalendarObject`, `getMultipleCalendarObjects` | `calendarQuery(filters)` returns URIs; the plugin re-checks, so the backend may over-return | optional [`SyncSupport`](https://github.com/sabre-io/dav/blob/master/lib/CalDAV/Backend/SyncSupport.php): `getChangesForCalendar(token, level, limit)` → added, modified, deleted, or `null` for an expired or unknown token |
| [go-webdav](https://github.com/emersion/go-webdav/blob/master/caldav/server.go) (Go) | path; principal from the context | `ListCalendarObjects` | `GetCalendarObject` | `QueryCalendarObjects` with a structured `CompFilter` (name, start, end, props, comps) | optional `SyncBackend` in open [PR #226](https://github.com/emersion/go-webdav/pull/226): token + limit → token, updated, deleted, or `ErrInvalidSyncToken` (403); the token doubles as ctag |
| [Radicale](https://github.com/Kozea/Radicale/blob/master/radicale/storage/__init__.py) (Python) | href in a collection | `get_all` | `get_multi(hrefs)` | `get_filtered(filters)` | `sync(old_token)` → token, changed hrefs; token is a SHA-256 over hrefs and history ETags, snapshot on disk per token, 30-day expiry ([sync.py](https://github.com/Kozea/Radicale/blob/master/radicale/storage/multifilesystem/sync.py)) |
| [Shiny](https://github.com/shinyorg/aspnet/blob/main/src/Shiny.AspNet.CalDav/IDavCollectionStore.cs) (C#) | principal, collection, name | `GetObjectsAsync` → name, ETag, modified, UID, length; called on every sync | `GetObjectAsync`; `PutObjectAsync` returns `null` when not stored byte for byte | none; filtered in memory | none in the store; derived from listings in memory |

## Sync tokens on PostgreSQL

- **sabre/dav PDO.** `calendars.synctoken` is a counter;
  `calendarchanges(uri, synctoken, calendarid, operation)` indexed on `(calendarid, synctoken)`
  ([pgsql schema](https://github.com/sabre-io/dav/blob/master/examples/sql/pgsql.calendars.sql)). A write inserts a
  change row with the current token, then increments it; operation 1, 2, 3 is added, modified, deleted, and a
  deletion removes the object row and leaves the change row as the tombstone. Reading selects
  `synctoken >= token` ordered, collapses to the last operation per URI, and returns the last token + 1, with a
  truncation flag at the limit ([PDO.php](https://github.com/sabre-io/dav/blob/master/lib/CalDAV/Backend/PDO.php)).
  The two write statements are not in a transaction there (**inference**: concurrent writers can interleave). It
  prunes nothing.
- **Nextcloud.** The same table plus `created_at`
  ([migration](https://github.com/nextcloud/server/blob/master/apps/dav/lib/Migration/Version1025Date20240308063933.php));
  writes and reads run in a transaction, and the read is bounded by `synctoken < current` so the answer matches the
  token returned ([CalDavBackend.php](https://github.com/nextcloud/server/blob/master/apps/dav/lib/CalDAV/CalDavBackend.php)).
  A daily job prunes change rows beyond 10 000 newest and older than 60 days
  ([PruneOutdatedSyncTokensJob](https://github.com/nextcloud/server/blob/master/apps/dav/lib/BackgroundJob/PruneOutdatedSyncTokensJob.php)).
- **The gap.** Neither rejects a token older than the oldest retained change; sabre's interface requires `null` for
  an expired token, but its PDO backend never returns it, and no Nextcloud code path raising `InvalidSyncToken` was
  found (**absence**). A pruned token most likely gets an incomplete delta instead of `valid-sync-token`
  (**inference**). RFC 6578 allows invalidation and expects a full resync on it
  ([§3.2](https://www.rfc-editor.org/rfc/rfc6578#section-3.2)).
- **Design consequence (inference).** A per-calendar `sync_seq`, incremented with
  `UPDATE … RETURNING` in the write transaction, serialises writers per calendar across nodes without a global
  sequence. A change table keyed `(calendar_id, seq)` holds tombstones; reads use the bounded window
  `seq > token AND seq <= current`. A per-calendar low-water mark, raised in the same transaction that prunes, turns a
  token below it into 403 `valid-sync-token`. The counter doubles as `CS:getctag`, as in go-webdav PR #226, and the
  token is a URI carrying calendar and seq, like sabre's `http://sabre.io/ns/sync/<n>`
  ([Sync Plugin](https://github.com/sabre-io/dav/blob/master/lib/DAV/Sync/Plugin.php)). A second Postgres reference
  is closureOSS/calendare's `SyncJournal` with GUID URI tokens
  ([SyncJournal.cs](https://github.com/closureOSS/calendare/blob/master/Data/Models/SyncJournal.cs)).

## Conformance tools

- **Apple CalDAVTester** ([repo](https://github.com/apple/ccs-caldavtester)): an XML-scripted request and verify
  suite, Apache-2.0, archived since 2024-02-24. Its scripts (`well-known.xml`, `current-user-principal.xml`,
  `options.xml`, `propfind.xml`, `ctag.xml`, `sync-report.xml`, `reports.xml`, `timezones.xml`, `conditional.xml`)
  remain usable ([scripts](https://github.com/apple/ccs-caldavtester/tree/master/scripts/tests/CalDAV)); the Python
  version it needs is **unverified**, likely Python 2 era, so expect to patch.
- **caldav-server-tester** (python-caldav, not DAVx5): probes a server and rates each feature as full, quirk,
  fragile, broken or unsupported; its output matches python-caldav's
  [`compatibility_hints.py`](https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py#L880-L925),
  a feature matrix with profiles for about twenty servers. Licence: the README says AGPL-3.0-or-later, the GitHub
  sidebar GPL-3.0 ([GitHub](https://github.com/python-caldav/caldav-server-tester),
  [PyPI](https://pypi.org/project/caldav-server-tester/)). It writes data, so a read-only server reports
  "unsupported" for write features. DAVx5 publishes no server tester (**absence**).
- **Shiny.AspNet.CalDav** runs in process on ASP.NET Core and can serve as an oracle for a CalDAV client in tests.
- Baïkal (sabre/dav in Docker) and Radicale are further oracles
  ([Baikal](https://github.com/sabre-io/Baikal), [Radicale](https://github.com/Kozea/Radicale)); python
  `caldav` and [tsdav](https://github.com/natelindev/tsdav) are clients that can drive a server.

## Inference

My reading of the above, for a framework package; not a sourced fact.

- **Build both the server and the client.** No .NET library is AOT-clean, maintained and free of Ical.Net at once,
  and the protocol layer is small next to the quirks it must absorb.
- **One XML codec for both directions**, on a hardened `XmlReader` and an async `XmlWriter`; no `XmlSerializer`, and
  no `XDocument.Load` on a request stream.
- **Keep sync state in PostgreSQL**: counter, change table with tombstones, low-water mark, 403 `valid-sync-token`
  below it, counter as ctag, token as a URI. An in-memory token fails the ten-nodes-on-one-Postgres test.
- **Publish an ICS feed beside CalDAV**, since Outlook and Google Calendar subscribe to a URL and do not speak
  CalDAV. Google subscribes only to a public URL, so the feed likely needs a secret-token URL rather than a login.
- **App passwords over Basic.** Every surveyed client speaks Basic (Evolution **unverified**); only Google, as a
  server, requires OAuth. Per-device app passwords behind a validator seam follow the iCloud, Fastmail and Nextcloud
  precedents.
