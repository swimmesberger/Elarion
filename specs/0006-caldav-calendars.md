---
type: Specification
title: "Spec 0006: CalDAV calendars"
description: "What Elarion's CalDAV support must do: publish an application's calendars read-only to iOS, macOS, Thunderbird and Android calendar apps and as ICS feeds, authenticate those apps with app passwords, and sync external CalDAV calendars into an application one step at a time, under NativeAOT."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T18:00:00Z }
status: stable
---
# Spec 0006: CalDAV calendars

This spec describes the new opt-in packages `Elarion.WebDav`, `Elarion.WebDav.EntityFrameworkCore`,
`Elarion.WebDav.AspNetCore`, `Elarion.CalDav`, `Elarion.AppPasswords`, `Elarion.AppPasswords.EntityFrameworkCore` and
`Elarion.AppPasswords.AspNetCore`. Why it is shaped this way, and what was rejected, is in
[ADR-0111](../docs/decisions/0111-caldav.md). The calendar model, reader, writer and recurrence engine are
`Elarion.Calendars` ([ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md), spec 0004).

## Intent

A developer lets people see the application's calendars (shifts, bookings, appointments, deadlines) in the calendar
app they already use. A module publishes calendar objects in the handler that changes its data and declares a calendar
source, an ordinary handler that returns the calendars the current user may see, so authorization, resource rules,
feature gates and module enablement decide what a calendar app shows exactly as they decide what the application
shows. A person creates an app password in the application, types the host name, their e-mail and that password into
iOS, macOS, Thunderbird or DAVx5, and sees the calendars, read-only, kept in sync within the client's polling interval.
Someone on Outlook or Google Calendar subscribes to a feed URL instead. The other way round, an application syncs a
calendar from an external CalDAV server (iCloud, Nextcloud, Fastmail, Google) one step at a time and keeps what changed.
Everything works on several nodes over one PostgreSQL and in an application published with NativeAOT.

## User scenarios

### Publish a calendar a person sees on their phone (P1)

- Given a module that publishes a shift as an event in its create-shift handler, when the handler's transaction
  commits, then the event is in the published calendar; when the transaction rolls back, then it is not.
- Given a published event, when the handler publishes the same event again unchanged, then the calendar's sync token
  does not change.
- Given a published event, when the handler publishes it with a changed start, then the sync token changes, the
  object's ETag changes, and a client syncing from the previous token receives that object as changed.
- Given a published event, when the handler removes it, then a client syncing from the previous token receives it as
  removed.
- Given a calendar with 300 published events, when the application replaces the calendar's content with 299 of them,
  one of them changed, then a client syncing from the previous token receives exactly one change and one removal.
- Given a recurring event with a weekly rule and one moved occurrence, when it is published, then it is one calendar
  object holding the series and the moved occurrence under one UID, with a `VTIMEZONE` for its time zone.

### Decide who sees which calendar in a handler (P1)

- Given a calendar source `shifts` whose handler requires the permission `shifts.read` and returns one calendar per
  user ("My shifts"), when a user without that permission connects, then their calendar home lists no shift calendar
  and the shift calendar's URL answers `404`.
- Given the same source, when a user with the permission connects, then their home lists exactly their own shift
  calendar, with the display name, colour and description the handler returned.
- Given a team calendar the source returns to the members of a team through a resource rule, when a member leaves the
  team, then on the next sync the calendar is gone from that person's home.
- Given the source's module disabled by configuration, or the source's handler behind a closed feature gate, when a
  user connects, then no calendar of that source is listed or served.
- Given a principal URL or a calendar home URL with another user's id, when a user requests it, then the answer is
  `404`.

### Add an account in a calendar app (P1)

- Given a host serving CalDAV under `/dav`, when a person adds a CalDAV account on iOS with only the host name, their
  e-mail and an app password, then the account is created and the calendars of the person's home appear.
- Given the same host, when the account is added in macOS Calendar, Thunderbird, DAVx5 and Evolution, then each shows
  the same calendars.
- Given a request without credentials to any DAV URL, when it arrives, then the answer is `401` with
  `WWW-Authenticate: Basic realm="…", charset="UTF-8"`.
- Given a published calendar, when a client asks for its privileges, then it receives `read` and
  `read-current-user-privilege-set` only, and Thunderbird, DAVx5 and iOS show the calendar as read-only.
- Given a read-only calendar, when a client sends `PUT`, `DELETE`, `PROPPATCH`, `MKCALENDAR`, `MOVE` or `COPY`, then the
  answer is `403` with the `need-privileges` precondition, and nothing changes.
- Given a client holding a sync token older than the journal's retention, when it syncs, then the answer is `403` with
  `valid-sync-token` in the body, and the client resyncs the calendar in full and ends with the same objects.
- Given two nodes behind a load balancer, when a client syncs alternately against both, then each answer continues the
  sync of the previous one.
- Given a `calendar-query` for one week, when the calendar holds a weekly event that started a year ago and has no end,
  then the event is in the answer; a single event from last month is not.

### Subscribe to a feed from Outlook or Google Calendar (P1)

- Given a published calendar, when the person creates a feed link for it in the application, then they get one URL
  that Outlook and Google Calendar can subscribe to without a password, which returns the calendar as one `.ics`
  document.
- Given a feed link, when its app password is revoked, or the person no longer sees the calendar through its source,
  then the URL answers `404`.

### Manage app passwords (P1)

- Given a signed-in person, when the application's own-account handler creates an app password named "iPhone", then
  the secret is returned once and is never readable again; the list shows the name, the creation time and the last
  use.
- Given an app password in use, when the person revokes it, then the next request with it is refused with `401`.
- Given a person whose account is deactivated, when their calendar app polls, then the request is refused.
- Given an app password, when it is sent to the application's browser surface (`/rpc`), then it does not authenticate.
- Given a person who already holds the maximum number of app passwords, when they create another, then the creation is
  refused with a coded error.
- Given an application that configures the scheme to require the Basic user name to be the account's e-mail, when a
  calendar app sends the right app password with another user name, then the request is refused with `401`.
- Given an application whose credentials live in its own service, when it replaces the credential check with a
  delegate that asks that service, then calendar apps authenticate against it and Elarion's app-password store is not
  used.

### Sync an external calendar into the application (P2)

- Given an account on a CalDAV server and its credentials, when the application discovers the account from the
  server's base URL, then it receives the account's calendars with their names, colours and component kinds.
- Given a calendar and no state, when the application runs a sync step, then it receives every object of the calendar,
  read into calendar items, and a state it stores.
- Given the stored state, when the application runs the next step after one event changed and one was deleted on the
  server, then it receives exactly that change and that removal, and a new state.
- Given a server whose token expired, or which offers no `sync-collection`, when the application runs a step, then it
  receives the changes and removals computed by comparing ETags, and the same final objects as with a token.
- Given an object on the server that is not valid iCalendar, when the application runs a step, then the step succeeds,
  and that object comes back with its located, coded errors.
- Given a server that refuses the credentials, when the application runs a step, then the result says so with a code,
  and the state is unchanged.
- Given Google's CalDAV endpoint and a credential delegate that returns an OAuth bearer token, when the application
  syncs a Google calendar, then it works as with any other server.
- Given an event the application changed, when it writes it back with the ETag it last saw, then the server stores it;
  when someone changed it on the server meanwhile, then the write is refused as a conflict with a code.

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

Packages and shape

- FR-001: The feature MUST ship as the seven packages named at the top of this spec, with the dependencies ADR-0111
  states; `Elarion.WebDav` and `Elarion.CalDav` MUST NOT depend on ASP.NET Core or EF Core, and no package MUST expose a
  type of a third-party library in its public API.
- FR-002: `Elarion.WebDav`, `Elarion.CalDav` and `Elarion.AppPasswords` MUST be AOT-compatible, and serving and syncing
  calendars MUST NOT use reflection, so that an application published with NativeAOT gets no trimming or AOT warning
  from them.
- FR-003: Registration MUST be `AddElarionWebDav`, `AddElarionCalDav` and `AddElarionAppPasswords`, each taking an
  optional `IConfiguration` and an optional options callback, with limits and defaults in one options bag per package.

Publishing

- FR-004: `ICalendarPublisher` MUST publish an object (an `Elarion.Calendars` event, task or component tree under one
  UID), remove an object by UID, and replace the content of a calendar with a set of objects, addressed by calendar
  source and calendar key.
- FR-005: Publishing MUST take part in the caller's unit of work, so the change is visible to clients exactly when the
  caller's transaction commits.
- FR-006: Publishing an object whose written bytes equal the stored ones MUST NOT change the calendar's sync token.
- FR-007: Every other publish, remove and replace MUST change the calendar's sync token, and replacing MUST record each
  object it adds, changes and removes.
- FR-008: An object MUST be written with the `Elarion.Calendars` writer, with one `VTIMEZONE` per `TZID` it uses, and
  its ETag MUST be strong and derived from its bytes.
- FR-009: Publishing an object whose components have different UIDs, or that holds a `METHOD` property, MUST throw,
  because it is the application's mistake, not a user's input.

Calendar sources and access

- FR-010: A module MUST declare a calendar source as a query request implementing `ICalendarSource<TSelf>` (which
  states the source's name) whose handler returns the calendars of that source the current user may see, each with a
  key, a display name, and optionally a colour, a description, the component kinds and a time zone, and MUST register
  it with `AddElarionCalendarSource<TQuery>()` in the module's `ConfigureServices`.
- FR-011: Every DAV request MUST invoke the calendar sources through the handler pipeline as the authenticated user,
  so `[Require*]`, resource rules and feature gates apply, and MUST serve only calendars a source returned.
- FR-012: A calendar no source returns, a source whose module is disabled or whose feature gate is closed, and a
  principal or home URL of another user MUST answer `404`.
- FR-013: Two calendar sources with the same name, and a source name or calendar key that is not 1 to 64 characters of
  letters, digits, `-` and `_`, MUST fail at startup or when returned, naming the source; publishing into a source that
  is not registered MUST throw.

The CalDAV server

- FR-014: The host MUST serve, under a configurable prefix that defaults to `/dav`, the principal
  `/dav/principals/{user}/`, the calendar home `/dav/calendars/{user}/`, each calendar at
  `/dav/calendars/{user}/{source}.{key}/` and each object below it as `{name}.ics`.
- FR-015: `/.well-known/caldav` at the application root MUST redirect `PROPFIND`, `GET`, `HEAD` and `OPTIONS` with `301`
  to the prefix.
- FR-016: `OPTIONS` on every DAV URL MUST answer `200` with `DAV: 1, 3, calendar-access` and an `Allow`
  header that lists no write method; the server MUST NOT advertise `calendar-auto-schedule` or `calendar-schedule`.
- FR-017: `PROPFIND` with depth 0 and 1 MUST answer the properties RFC 4918, RFC 4791, RFC 5397 and RFC 6578 define for
  the principal, the home and calendars (among them `current-user-principal`, `principal-URL`, `calendar-home-set`,
  `calendar-user-address-set`, `displayname`, `resourcetype`, `supported-calendar-component-set`, `supported-report-set`,
  `current-user-privilege-set`, `owner`, `sync-token`, `getetag`, `getcontenttype`, `calendar-timezone`), and
  `getctag` and `calendar-color`, and MUST answer an unknown property with `404` inside the `multistatus`.
- FR-018: `PROPFIND` with depth `infinity` MUST answer `403` with the `propfind-finite-depth` precondition.
- FR-019: `REPORT` MUST support `sync-collection` (RFC 6578), `calendar-multiget`, `calendar-query` with
  `comp-filter`, `prop-filter`, `param-filter`, `text-match`, `time-range`, `expand` and `limit-recurrence-set`, and
  `free-busy-query` (RFC 4791).
- FR-020: The sync token MUST be a URI, MUST be valid on every node and after a restart, and a token older than the
  journal's retention or foreign to the calendar MUST answer `403` with `valid-sync-token` in the body.
- FR-021: The journal's retention MUST be an option, and a calendar's journal MUST NOT grow past its retention while
  the calendar keeps changing, without a job the application has to schedule.
- FR-022: The `time-range` test MUST follow the table of RFC 4791 section 9.9 for every component kind, including
  recurring components and floating times, which MUST be evaluated in the calendar's time zone.
- FR-023: Every calendar MUST grant `read` and `read-current-user-privilege-set` only, and every write method MUST
  answer `403` with the `need-privileges` precondition and change nothing.
- FR-024: `GET` and `HEAD` on an object MUST answer its bytes with `Content-Type: text/calendar; charset=utf-8` and its
  `ETag`, and MUST honour `If-None-Match`.
- FR-025: A request body MUST be read with DTDs and external entities refused, and with limits on size and nesting that
  are options; a body over a limit MUST answer `413` or `400` without reading the rest.

Feeds

- FR-026: Every published calendar MUST be servable as one `text/calendar` document at a feed URL under the prefix whose
  path holds a feed token, and the feed MUST answer `404` when the token is unknown, revoked, bound to another calendar,
  or its user no longer sees the calendar through its source.
- FR-027: A feed document MUST hold each `VTIMEZONE` once and the same objects as the calendar.

App passwords

- FR-028: `IAppPasswords` MUST create an app password for a user with a name and a scope (`dav` or one calendar for a
  feed token) and return its secret once, list a user's app passwords without their secrets, and revoke one of a user's
  app passwords, a foreign or unknown id being not found.
- FR-029: A secret MUST hold at least 256 bits from a cryptographically secure generator after a configurable prefix,
  and only a hash of it MUST be stored.
- FR-030: Creating an app password beyond the configured maximum per user MUST fail with the code
  `app_password.limit_reached`.
- FR-031: `Elarion.AppPasswords.AspNetCore` MUST provide an authentication scheme that accepts an app password with
  scope `dav` over HTTP Basic and a feed token in a feed URL, MUST build the principal on every request through the
  `OnCreatePrincipal` event, and MUST refuse a revoked password and a user for whom that event returns no principal.
- FR-032: The scheme's events MUST let an application replace the credential check (`OnVerifyCredential`), check the
  Basic user name (`OnValidateUserName`, by default not checked), create the principal (`OnCreatePrincipal`) and
  observe a success (`OnAuthenticated`), each as a delegate over a context that carries the request's services, and
  MUST work under NativeAOT.
- FR-033: The DAV route group MUST accept only the schemes the host names for it, so an app password MUST NOT
  authenticate any other endpoint and a browser session MUST NOT authenticate a DAV request.
- FR-034: A missing or refused credential MUST answer `401` with a Basic challenge, and an authenticated request the
  sources refuse MUST answer `403` or `404` without a challenge.
- FR-035: The last use of an app password MUST be recorded at most once per configurable interval.
- FR-036: The DAV routes MUST be exempt from Elarion's cross-origin protection, and MUST NOT carry the application's
  browser credential.

The client

- FR-037: `CalDavClient` MUST discover an account from a base URL through `/.well-known/caldav`,
  `current-user-principal` and `calendar-home-set`, and MUST list its calendars with their display names, colours,
  component kinds and whether they are writable.
- FR-038: `SyncAsync(calendar, state)` MUST return the changed objects read by the `Elarion.Calendars` reader, the
  removed object names, and a new opaque state that serializes to a string.
- FR-039: A sync step MUST use `sync-collection` when the server offers it, and MUST fall back to comparing ETags when
  it does not or when the server refuses the token.
- FR-040: Credentials MUST come from a delegate that returns an `Authorization` header value per request.
- FR-041: A server's refusal of credentials, a calendar that no longer exists, and an object that is not valid
  iCalendar MUST be results with codes in the `caldav.*` form, not exceptions; only cancellation, misconfiguration and
  transport failures MUST throw.
- FR-042: `PutAsync` and `DeleteAsync` MUST send `If-Match` (or `If-None-Match: *` to create) and MUST report a failed
  precondition as the code `caldav.conflict`.
- FR-043: The client MUST bound response sizes, objects per step and XML nesting by options.

## Success criteria

- SC-001: An account added with only host name, e-mail and app password shows the published calendars on iOS, macOS,
  Thunderbird, DAVx5 and Evolution, read-only, verified once by hand per client before release.
- SC-002: The conformance scripts named in the research note pass for discovery, `OPTIONS`, `PROPFIND`,
  `sync-collection`, `calendar-multiget` and `calendar-query`.
- SC-003: A change published in a handler reaches a polling client in one sync, and a client that syncs 10,000 times
  against an unchanged calendar causes no write to the database.
- SC-004: Answering a sync of an unchanged calendar of 1,000 objects takes under 20 ms on the benchmark machine.
- SC-005: A sample application published with NativeAOT serves calendars and feeds without trimming or AOT warnings.

## Out of scope

- Two-way editing from calendar apps (`PUT`/`DELETE` into an application's data); ADR-0111 describes its seam.
- CalDAV scheduling (RFC 6638, inbox and outbox, iTIP, iMIP mail) and free-busy across users.
- Push to clients (Apple push, WebDAV-Push); clients poll.
- Microsoft 365 and Exchange through Microsoft Graph.
- Stored external accounts, credential encryption and a framework sync job for the client; the application stores the
  state and schedules the step.
- CardDAV; it follows as its own spec on the same WebDAV core.
- A store for the EF-free `Elarion.Sql` tier.
- Managing app passwords through framework handlers or UI; the application writes its own-account handlers.

## Decisions

- [ADR-0111](../docs/decisions/0111-caldav.md): this feature's design.
- [ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md): the iCalendar model it builds on.
- [ADR-0108](../docs/decisions/0108-spreadsheet-records.md): the design language of file-format packages.
