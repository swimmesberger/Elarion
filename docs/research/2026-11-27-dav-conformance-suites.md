---
type: Research note
title: "DAV conformance: what other servers' and clients' test suites check, and what porting them found in Elarion"
description: "The edge cases of Apple's CalDAVTester, litmus, sabre/dav and sabre/vobject, Radicale, Xandikos, Cyrus, python-caldav, go-webdav, dav4jvm and vdirsyncer, ported as Elarion's own WebDAV, CalDAV, CardDAV and recurrence tests; the faults they found and the answers Elarion pins where those projects disagree."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T18:00:00Z }
status: draft
sources:
  - https://github.com/apple/ccs-caldavtester/tree/bed21e59
  - https://github.com/notroj/litmus/tree/58c1c68e
  - https://github.com/sabre-io/dav/tree/be1d58d6bce7ca33313b02909f9088c93c35db02
  - https://github.com/sabre-io/vobject/tree/5e2f338811916c03927398bb24e527989de63194
  - https://github.com/Kozea/Radicale/tree/977deaa
  - https://github.com/jelmer/xandikos/tree/af4e6ab
  - https://github.com/cyrusimap/cyrus-imapd/tree/9d64d90/cassandane/tiny-tests
  - https://github.com/python-caldav/caldav/tree/cb032bf8668ef547f1f3e882f599a2586c82e327
  - https://github.com/python-caldav/caldav/blob/cb032bf8668ef547f1f3e882f599a2586c82e327/caldav/compatibility_hints.py
  - https://github.com/python-caldav/caldav-server-tester/tree/709df50eb3e705f4e3b3bcafd8bf2faa72806c48
  - https://github.com/emersion/go-webdav/tree/dda260f433a578f58d2b61fa8734d1516baa0da5
  - https://github.com/bitfireAT/dav4jvm/tree/493335bb6c00e38fffe7f14f09e126ecfa608446
  - https://github.com/pimutils/vdirsyncer/tree/f8328610e458fbcdb92e20c14412911afc1d974c
---
# DAV conformance: what other projects' suites check, and what porting them found

Elarion's WebDAV, CalDAV and CardDAV packages were written from the RFCs and a client survey
([CalDAV clients](2026-11-26-caldav-servers-clients-and-dotnet.md), [CardDAV clients](2026-11-27-carddav-clients.md)).
Mature projects have years of regression tests for what the RFCs leave unclear and what real clients and servers do.
On 2026-11-27 those suites were read (never run) at the commits in the frontmatter, about 1,000 cases were catalogued,
and the ones that apply to Elarion were ported as its own tests. The licences of the sources were not a concern for
this: the tests restate behaviour in Elarion's own code and cite the case they come from.

## What was read

| Group | Projects | Cases catalogued |
| --- | --- | --- |
| Conformance scripts | Apple's CalendarServer suite `ccs-caldavtester`, litmus | 237 |
| A server library | sabre/dav (Nextcloud, Baikal) and sabre/vobject | 352 |
| Other servers | Radicale, Xandikos, Cyrus (Cassandane) | 249 |
| Clients and cross-server tools | python-caldav and its `compatibility_hints.py` (183 server quirk flags), caldav-server-tester, go-webdav, dav4jvm (DAVx5), vdirsyncer | 170 + 183 flags |

Out of scope throughout: the success paths of writes, scheduling, sharing, ACL editing, quotas, locks.

## What was ported

About 420 test cases in eight files: `WebDav/DavConformanceTests.cs`, `CalDav/CalDavHostConformanceTests.cs`,
`CalDav/CalDavQueryConformanceTests.cs`, `CalDav/CalDavExpandConformanceTests.cs`,
`CalDav/CalDavClientConformanceTests.cs`, `CardDav/CardDavConformanceTests.cs` and
`Calendars/CalendarRecurrenceConformanceTests.cs`. Each test cites its case ids and projects. Cases already covered by
earlier tests were not duplicated.

## What they found

Faults, each fixed with its test:

- **Client, data loss.** A propstat without a status was read as a removal (dav4jvm, vdirsyncer). An href that a
  multiget leaves out, as every sabre-based server does, was never removed. The same href twice in a sync answer counted
  as both changed and removed (Cyrus, Stalwart). A truncated answer whose token did not move paged forever.
- **Client, security.** A redirect from https to http was followed with the credentials (python-caldav, dav4jvm).
- **Client, interoperability.** Members were fetched by a re-encoded href instead of the server's spelling (Robur);
  relative hrefs with a colon were read as URIs; a token refused with a bare 400 (Ox) failed the step; accounts without
  `current-user-principal` (Synology) or `calendar-home-set` (Nextcloud shares), or with several homes, were not found;
  unquoted or percent-encoded ETags were lost; 409 was not a conflict; a 207 to a write reporting a failure was a
  success; a 303 to a PUT repeated it; raw control characters in an answer failed the whole step.
- **Server, WebDAV.** The ASP.NET host decoded the request path twice (Apple M-09); a control character in calendar data,
  a card or a display name made the 207 writer throw halfway; an invalid `sync-level` answered 403 instead of 400; a
  PROPFIND with no or several of `propname`, `allprop` and `prop` was accepted; Evolution's escaped If-None-Match did not
  match.
- **Server, CalDAV.** A recurring VJOURNAL matched only its first instance; an absolute alarm trigger far before its
  event was missed; `expand` without a valid range ran to the engine's horizon; `novalue` was ignored; floating times
  expanded in the calendar's zone instead of the request's `C:timezone`; a query sent to an object's URL was refused; an
  unknown collation answered `valid-filter` instead of `supported-collation`; a time-range on VCALENDAR matched nothing.
- **Server, CardDAV.** A query sent to a card's URL was refused; NICKNAME and CATEGORIES were matched as one string.
- **Recurrence.** An UNTIL before DTSTART yielded no instance; BYWEEKNO without BYDAY expanded the whole week.

## Where the projects disagree: Elarion's pinned answers

| Question | Elarion | Others |
| --- | --- | --- |
| PROPFIND without Depth | 1 | Apple: infinity (then 403) |
| `sync-level` infinity (and DAVx5's `infinite`) | 403 `sync-traversal-supported` | sabre: read as 1 |
| Depth 2 or garbage | 400 | sabre: the default |
| `getcontentlength` on a collection | 404 propstat | Apple: 200 |
| `Prefer: depth-noroot`, `If-Modified-Since` on PROPFIND | ignored | Apple: honoured |
| A write | 403 `need-privileges` before any precondition; 404 below a missing parent | Apple: 404 first |
| calendar-query Depth 0 on a calendar | no members | sabre: 400; Apple: members |
| addressbook-query Depth 0 | the members (Evolution sends it) | sabre: missing Depth is 0 |
| Malformed calendar filter | 403 `valid-filter` | sabre: 400 |
| Floating time-range value | read as UTC | Apple: 403 |
| Unknown `match-type` in CardDAV | 403 `supported-filter` | Apple: contains |
| CardDAV param-filter | per property instance (RFC 6352) | sabre: across instances |
| RANGE=THISANDFUTURE override | replaces its own instance only | Apple: shifts later ones |
| An expand over the occurrence cap | the first instances, 207 | sabre throws, Radicale 400, Cyrus 422 |
| A 500 to a stored sync token (client) | an error, the caller keeps its state | CCS answers stale tokens with 500 |
| A missing component set (client) | events and to-dos | RFC 4791 §5.2.3: all |

## Deliberately not ported

Cases that need a feature Elarion does not have: the calendar home or a principal as a query or free-busy target, home
sync, `limit-freebusy-set`, `min/max-date-time`, `calendar-no-timezone`, custom VTIMEZONE bodies (the publisher writes
the zones), export query parameters, typed write preconditions, Negotiate authentication, rate limits, batching
multigets after a 507. Client cases about queries, free-busy or expand, which the client never sends.

## Limits

The suites were read, not run against Elarion: a case was ported only when its expectation could be stated without the
source project's fixtures beyond what the catalogue quotes. Apple's client behaviour still rests on server logs and bug
reports, as in the client notes.
