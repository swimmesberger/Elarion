---
type: Architecture Decision Record
title: "ADR-0111: CalDAV — published calendars served from a store, calendar sources as handlers, app passwords, and a sync-step client"
description: "Elarion serves an application's calendars over CalDAV and as ICS feeds from a framework-owned store the application publishes to in its own transaction, decides who sees which calendar in an ordinary handler, authenticates calendar apps with app passwords over HTTP Basic, and syncs external CalDAV calendars one step at a time, on a WebDAV core that CardDAV can reuse."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T17:30:00Z }
status: draft
---
# ADR-0111: CalDAV — published calendars served from a store, calendar sources as handlers, app passwords, and a sync-step client

- Status: Proposed
- Date: 2026-11-26
- Related: [ADR-0109](0109-icalendar-model-reader-and-writer.md) (the iCalendar model, reader, writer and recurrence
  engine this builds on; `Elarion.Calendars`), [ADR-0110](0110-vcard-contacts.md) (vCard; CardDAV later reuses the
  WebDAV core decided here), [ADR-0108](0108-spreadsheet-records.md) (the design language of file-format packages),
  [ADR-0013](0013-resource-and-data-level-authorization.md) and
  [ADR-0095](0095-one-resource-rule-for-list-point-check-and-complement.md) (resource rules the calendar sources use),
  [ADR-0016](0016-feature-flag-gating.md) (feature gates), [ADR-0017](0017-dependency-light-core.md) (opt-in
  siblings), [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node tier),
  [ADR-0054](0054-device-identity-and-provisioning.md) (the other credential Elarion issues),
  [ADR-0084](0084-authenticating-proxy-identity.md) (sign-in at an edge, which calendar apps cannot follow), the
  research note [CalDAV clients and .NET libraries](../research/2026-11-26-caldav-servers-clients-and-dotnet.md), and
  [spec 0006](../../specs/0006-caldav-calendars.md).

## Context

Several products built on Elarion need calendars that leave the application: shifts, bookings, appointments and
deadlines that people want to see on their phone next to their private appointments, and, the other way round,
external calendars an application reads to plan around people's time. The first product use case is the first half,
read-only: people see what the application owns, and change it only in the application. Two-way editing and syncing
external calendars in follow; the design has to hold for them without a rewrite.

What the clients require is fixed by the clients, not by us (the research note has the evidence):

- **The calendar apps people use speak CalDAV, or only ICS subscriptions.** iOS, macOS, Thunderbird, DAVx5 on Android
  and Evolution add a CalDAV account from a host name (`/.well-known/caldav`, then `current-user-principal`, the
  calendar home set, the calendars). Outlook in every form and Google Calendar cannot add a CalDAV account at all; they
  subscribe to an `.ics` URL, which cannot carry a password.
- **Sync must be exact.** Clients poll with the RFC 6578 `sync-collection` report (or a CTag plus ETag comparison), so a
  server must say which objects changed and which were deleted since a token, survive restarts and answer the same on
  every node. An expired token must be refused in the form each client recognises (`403` with `valid-sync-token` in
  the body), or the client keeps a stale copy or retries forever.
- **Read-only must be said, not implied.** A calendar without `current-user-privilege-set`, or with any write privilege
  in it, is treated as writable, and the user's edits fail later.
- **Every client speaks HTTP Basic; none follows a sign-in redirect.** Only Google's own server needs OAuth, and only
  DAVx5 offers it. An application that signs people in at an edge or through an identity provider has no password a
  calendar app could send, and must not hand its sign-in credential to a device that stores it and replays it on
  every poll.

A consuming application built a read-only CardDAV server by hand and shows both what works and what hurts. Its app
passwords (256-bit random secrets, only a SHA-256 hash stored, revoked by deleting the row, a cap per user, the last
use written at most every fifteen minutes, a second authentication scheme only on the DAV routes, no lockout because
a lockout keyed on the user name lets anyone lock a colleague out) worked well and are adopted here. Its sync did not:
it serves only a CTag computed over the visible rows, because a sync token needs a durable change log that also
records deletions and visibility losses, and the application had none. Its endpoints had to seed the dispatch scope
by hand after the late Basic authentication, and its host had to exempt the DAV paths from cross-origin protection.

No .NET library fits. The only CalDAV server library that is AOT-compatible and covers the reports is six weeks old,
keeps sync tokens in memory and carries its own iCalendar parser; the CalDAV clients all depend on Ical.Net, which is
not AOT-safe (ADR-0109). Elarion builds both sides, on its own iCalendar package.

## Decision

### 1. Packages: a WebDAV core, CalDAV on it, an ASP.NET host, and app passwords

| Package | Depends on | Holds |
| --- | --- | --- |
| `Elarion.WebDav` | `Elarion.Abstractions`, `Microsoft.Extensions` abstractions | The WebDAV protocol without a wire: the hardened XML reader and writer, `multistatus`, `PROPFIND`/`REPORT`/`OPTIONS` handling, principals and home sets, `sync-collection`, ETag preconditions, the published-collection store seam and the collection-source seam. Shared with CardDAV later. |
| `Elarion.WebDav.EntityFrameworkCore` | `Elarion.WebDav`, EF Core relational | The store: published collections, their objects and a change journal, mapped onto the application's `DbContext` by a bundled generator, so publishing joins the application's transaction. |
| `Elarion.WebDav.AspNetCore` | `Elarion.WebDav`, `Elarion.AspNetCore` | The host: one route group for every registered DAV protocol, `/.well-known/caldav` (and later `carddav`) at the application root, the WebDAV methods, dispatch-scope seeding after authentication, the cross-origin exemption. |
| `Elarion.CalDav` | `Elarion.WebDav`, `Elarion.Calendars` | CalDAV: calendar sources and the publisher, the calendar reports (`calendar-query`, `calendar-multiget`, `free-busy-query`), the ICS feed, and the CalDAV client. |
| `Elarion.AppPasswords` | `Elarion.Abstractions`, `Microsoft.Extensions` abstractions | Issuing, listing, revoking and verifying app passwords, and the principal seam. BCL cryptography only. |
| `Elarion.AppPasswords.EntityFrameworkCore` | `Elarion.AppPasswords`, EF Core relational | The app-password store, mapped by a bundled generator. |
| `Elarion.AppPasswords.AspNetCore` | `Elarion.AppPasswords`, `Elarion.AspNetCore` | The ASP.NET authentication scheme: HTTP Basic, and a feed token in the URL path. |

The protocol packages are `IsAotCompatible` and have no outside dependency. The host depends on the protocol, never
the reverse; CalDAV registers itself into the WebDAV dispatcher, so the host knows no protocol by name. The client
lives in `Elarion.CalDav` and takes an `HttpClient` from the application; it binds no host. The stores follow the EF
tier of `Elarion.Blobs.PostgreSql`, `Elarion.Devices.EntityFrameworkCore` and `Elarion.WebPush.EntityFrameworkCore`; an
EF-free store for the `Elarion.Sql` tier is a later sibling behind the same seam.

### 2. The server serves a store the application publishes to

The application does not answer CalDAV requests. It **publishes calendar objects** into a framework-owned store, in
the handler transaction that changed its own data, through `ICalendarPublisher`: publish an object (an
`Elarion.Calendars` event, task or component tree under one UID), remove one, or replace a whole calendar's content
with a set (which removes what the set lacks). The publisher writes the object with the `Elarion.Calendars` writer;
its strong ETag is a hash of the bytes. Publishing bytes equal to the stored ones changes nothing, so re-publishing
after every save, or re-projecting a whole calendar at night, costs no client a refetch.

Every change to a calendar increments its counter and appends a journal row (object name, change, counter) in the same
transaction, taking the calendar's row lock with `UPDATE … RETURNING`. That counter is the RFC 6578 sync token (as a
URI, `urn:elarion:sync:<calendar>:<n>`, because Evolution treats a token without `:` or `/` as a CTag), the CTag, and
the input of `getctag`. Each change also removes that calendar's journal rows older than a retention and raises the
calendar's low-water mark, so a journal grows only while its calendar changes and needs no job; a token below the mark
gets `403` with `valid-sync-token`, and the client resyncs in full. Removals are journal rows,
so a deleted object disappears from every client.

At publish time the store also records each object's first start and last end over all occurrences (unbounded for an
endless recurrence), from the `Elarion.Calendars` recurrence engine. A `calendar-query` with a time range selects its
candidates by these bounds and then applies the exact RFC 4791 test to them, so no query parses the whole calendar.

Rejected: **handler-backed serving**, where the application writes "changes since token" and "get objects" handlers
over its own tables. Every application would rebuild the change journal, the tombstones and the token expiry, which
is the part the consuming application could not build and every server in the research gets subtly wrong. Also
rejected: **a backend interface** in the style of sabre/dav or go-webdav, which the DAV layer calls directly: it puts
the decision who sees what outside the handler pipeline.

The store holds a copy of what the application already has. At the tier Elarion targets (one PostgreSQL, a few
thousand objects per calendar) that copy is small, and it is what makes sync exact without a change log in the
application.

### 3. Who sees which calendar is a handler: calendar sources

A module declares a **calendar source**: a query request implementing `ICalendarSource<TSelf>` (a static name) whose
handler returns the calendars of that source the current user may see (key, display name, colour, description, the
component kinds, the calendar's time zone), registered with `AddElarionCalendarSource<TQuery>()` in the module's
`ConfigureServices`, which a disabled module never runs. A static interface member instead of an attribute keeps the
registration free of reflection without a generator of its own; publishing into an unregistered source throws, so a
forgotten registration shows at the first publish. The handler is an ordinary handler: `[RequirePermission]`, resource rules with `WhereAuthorized`, and
`[FeatureGate]` apply as everywhere, and it belongs to its module, so a disabled module or a closed feature gate takes
its calendars away consistently. Every DAV request runs the sources through the handler pipeline as the authenticated
user and answers only for the calendars they returned; a calendar no source returns is `404`, never `403`, as is a
principal URL that is not the caller's own.

Access is decided per calendar, not per object. A calendar whose content differs between viewers (the private
appointments of others shown as "busy") is published as one calendar per viewer. That is the price of exact sync: a
visibility change of one object for one user would otherwise be a deletion only that user must see, which is the log
the consuming application could not keep. Losing access to a calendar needs no log: the calendar leaves the home set.

Calendars are read-only in this decision: `current-user-privilege-set` grants `read` and `read-current-user-privilege-
set` only (never `write-properties`, which Thunderbird before 154 took for write access), writes answer `403` with
`need-privileges`, and `Allow` lists no write method. Two-way editing will let a source name a command for `PUT` and
`DELETE`; the command's handler validates, authorizes and changes the application's data, and the application
re-publishes, so the store stays a projection. The PUT then answers without an ETag (RFC 4791 §5.3.4) because the
stored bytes are the writer's, not the client's.

### 4. The same calendars as ICS feeds

For Outlook and Google Calendar, every published calendar can also be served as one `.ics` document from the same
store, at a URL that carries a feed token. A feed token is an app password bound to one calendar (section 5): the URL
is the credential, so it is revocable on its own, and the feed checks on every request that the token's user still
sees the calendar through its source. The feed is the same bytes as the objects, one `VCALENDAR` with each time zone
once.

### 5. App passwords: a credential Elarion issues, checked by its own scheme

Calendar apps authenticate with **app passwords** over HTTP Basic. `Elarion.AppPasswords` issues them for a user and a
name ("iPhone"), each with a **scope**: `dav` for a CalDAV account, or one calendar for a feed token. The secret is a
configurable prefix plus 256 bits from the CSPRNG, URL-safe; only its SHA-256 is stored, in a unique index, and the
secret is returned once. Revoking deletes the row. A cap per user and the last-use throttle are options. A
high-entropy machine-generated secret needs no slow hash and no lockout; a host rate limiter may still guard the route.

`Elarion.AppPasswords.AspNetCore` adds the authentication scheme the DAV route group requires explicitly, beside
whatever the application uses for its browser (cookie, OIDC, proxy identity), so a browser session cannot drive DAV
and an app password cannot reach `/rpc`. It answers a missing credential with `401` and `WWW-Authenticate: Basic …
charset="UTF-8"` (iOS sends unauthenticated requests during discovery and fails on a `403`) and a forbidden one with
`403` without a challenge. The principal is built on every request by an `IAppPasswordPrincipalFactory` the
application provides (an ASP.NET Identity default ships), so a revoked password or a deactivated account stops syncing
at once. The Basic user name is not checked: the secret identifies the credential, and matching it against an e-mail
address would tie the package to a user directory that applications signed in through an identity provider do not
have.

Elarion so far did not issue credentials ("Elarion does not issue credentials" on the authorization page);
`Elarion.Devices` was the exception for devices. App passwords are the second, for people's devices. Management stays
the application's: it writes the own-account handlers and the page that shows a new secret once.

### 6. The client syncs one step at a time

The CalDAV client in `Elarion.CalDav` discovers an account from a URL (well-known, principal, home set), lists its
calendars, and syncs a calendar one step at a time: `SyncAsync(calendar, state)` returns the changed objects, read by
the `Elarion.Calendars` reader into results with located errors, the removed ones, and a new state. The state is an
opaque value the application stores where it keeps the rest of the account. A step uses `sync-collection` when the
server offers it and falls back to a CTag check and an ETag comparison when it does not or when the token expired.
`PUT` and `DELETE` take `If-Match`. Credentials come from a delegate that yields an `Authorization` header (an app
password, or an OAuth bearer token the application refreshes), so Google works without Elarion owning OAuth. What
the external server sent is input: a broken object is a coded error in the result, never an exception. The
application schedules the sync with its own job. Microsoft 365 has no CalDAV; Graph is a separate decision.

### 7. Hostile input is bounded, and the scale tier holds

Request bodies are read through the hardened XML reader (no DTD, no resolver, a size cap, a depth cap) and calendar
data through `Elarion.Calendars` with per-request limits; responses are written forward, objects streamed from the
store. All state is in the database, so every node answers every request alike. Clients poll; push (Apple's needs a
certificate only Apple issues, WebDAV-Push is a draft only DAVx5 implements) is out of scope.

## Consequences

- An application gets CalDAV and ICS feeds for what it already shows by publishing objects in its handlers and writing
  one query handler per calendar source. The sync semantics, privileges, discovery and client quirks are the
  framework's, and are tested once against the clients' known behaviour (the conformance scripts of the research note).
- The store duplicates data and must be kept current. A missed publish is a stale calendar until the next publish or
  re-projection; `Replace` exists for the nightly or on-demand re-projection that repairs it.
- Per-viewer content costs one calendar per viewer. At a few thousand users and a few hundred objects each the store
  stays small; beyond that tier the seam is the place to swap in a dedicated server.
- Seven packages instead of one. Each holds one concern and the application references only what it uses: a host
  that serves calendars references `Elarion.CalDav`, `Elarion.WebDav.AspNetCore`, `Elarion.WebDav.EntityFrameworkCore`
  and the three app-password packages; one that only syncs external calendars references `Elarion.CalDav`.
- CardDAV later is an `Elarion.CardDav` package with address-book sources and a publisher over the same store,
  journal, host and app passwords.
- Changing the `Elarion.Calendars` writer's output changes every ETag once, and every client refetches once.
- The glossary gains: calendar source, published calendar, app password, feed token.

## Rejected alternatives

- **Adopt Shiny.AspNet.CalDav**: in-memory sync tokens fail on restart and on a second node, its own iCalendar parser
  duplicates ADR-0109, and six weeks and one maintainer is too little for a dependency. It remains a test oracle.
- **CTag only, as the consuming application did**: correct but every change costs every client a full listing, and
  Evolution and DAVx5 prefer `sync-collection`.
- **Handler-backed serving and a backend interface**: section 2.
- **The application's sign-in credential over Basic**: the device stores it and replays it, it cannot be revoked per
  device, and applications signed in at an edge have none.
- **App passwords inside the DAV host**: feeds, CardDAV and future machine clients need the same credential.
- **Secret URLs without a credential store** for feeds: no per-feed revocation, and no check that the user still sees
  the calendar.
- **Managed external-calendar subscriptions** (stored accounts, encrypted credentials, a framework sync job): more
  framework than the first products need; the sync step makes them a thin layer an application or a later package
  adds.
