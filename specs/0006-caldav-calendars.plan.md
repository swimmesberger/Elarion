---
type: Plan
title: "Plan 0006: CalDAV calendars"
description: "How the CalDAV support of spec 0006 is built: projects, public surface, the published-collection store and its journal, the WebDAV dispatcher and host, calendar sources, feeds, app passwords, the client, tests and slices."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T18:30:00Z }
status: draft
---
# Plan 0006: CalDAV calendars

How [spec 0006](0006-caldav-calendars.md) is built. The decisions behind it are
[ADR-0111](../docs/decisions/0111-caldav.md); the calendar model is `Elarion.Calendars`
([ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md)), which must ship first or together with
slice 3. The evidence for every client rule below is the research note
[CalDAV clients and .NET libraries](../docs/research/2026-11-26-caldav-servers-clients-and-dotnet.md).

## Projects

| Project | Notes |
| --- | --- |
| `src/Elarion.WebDav` | `IsAotCompatible`. XML codec, dispatcher, properties, `sync-collection`, store seam, collection-source seam. |
| `src/Elarion.WebDav.EntityFrameworkCore` (+ `.Generators`) | Store and journal; `[GenerateElarionDavStore]` on the application's `DbContext` (the pattern of `[GenerateElarionResourceGrants]`). |
| `src/Elarion.WebDav.AspNetCore` | `MapElarionDav()`, `/.well-known/*`, method routing, scope seeding, cross-origin exemption. |
| `src/Elarion.CalDav` | `IsAotCompatible`. Calendar sources, `ICalDavPublisher`, calendar properties and reports, feeds, `CalDavClient`. |
| `src/Elarion.AppPasswords` | `IsAotCompatible`. `IAppPasswords`, hashing, the `IAppPasswordStore` seam. |
| `src/Elarion.AppPasswords.EntityFrameworkCore` (+ `.Generators`) | Store; `[GenerateElarionAppPasswords]`. |
| `src/Elarion.AppPasswords.AspNetCore` | `AddElarionAppPasswordAuthentication(o => o.Events…)`: the Basic and feed-token scheme with its events; the Identity default for `OnCreatePrincipal` as `AddElarionIdentityAppPasswordPrincipals<TUser>()`. |
| `tests/Elarion.Tests/WebDav`, `CalDav`, `AppPasswords` | One folder per package, as `Spreadsheets/`. |
| `samples/` | The NativeAOT sample (SC-005) gains a published calendar and a feed. |

All runtime projects build for `net10.0` and `net11.0`. No outside package beyond EF Core relational in the EF projects.

## Public surface

```csharp
// --- Elarion.CalDav: declaring a source (in a module) ---
public sealed record MyShiftCalendars : ICalDavSource<MyShiftCalendars> {
    public static string SourceName => "shifts";
}

[Handler]
[RequirePermission("shifts", Verbs.Read)]
public sealed class ListMyShiftCalendars(ICurrentUser user)
    : IHandler<MyShiftCalendars, Result<CalDavSourceListing>> {
    public ValueTask<Result<CalDavSourceListing>> HandleAsync(MyShiftCalendars query, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success(new CalDavSourceListing([
            new PublishedCalendarInfo(Key: $"u-{user.Id}", DisplayName: "My shifts") {
                Color = "#2E7D32", Components = CalendarComponentKinds.Event,     // Elarion.Calendars type
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"),  // a TimeZoneInfo (spec 0004 FR-003)
            },
        ])));
}

// module ConfigureServices
services.AddElarionCalDavSource<MyShiftCalendars>();

// --- publishing, inside a command handler ---
public sealed class CreateShift(AppDbContext db, ICalDavPublisher calendars) {
    public async ValueTask<Result<Guid>> HandleAsync(Command command, CancellationToken ct) {
        // ... write the shift
        await calendars.PublishAsync(new CalDavAddress("shifts", $"u-{shift.UserId}"), ToEvent(shift), ct);
        return Result.Success(shift.Id);
    }
}
// also: RemoveAsync(address, uid, ct); ReplaceAsync(address, IAsyncEnumerable<CalendarObject>, ct)
// CalendarObject is Elarion.Calendars' type (plan 0004): the items of one UID, master and overrides, implicit from a
// CalendarEvent, a CalendarTodo or a component tree with one UID.

// --- host ---
builder.Services.AddElarionWebDav(builder.Configuration);
builder.Services.AddElarionCalDav(builder.Configuration);
builder.Services.AddElarionAppPasswords(builder.Configuration);
builder.Services.AddAuthentication().AddElarionAppPasswordAuthentication();
app.MapElarionDav();                      // /dav, /.well-known/caldav, feeds; requires the app-password scheme

// --- app passwords, inside the application's own-account handlers ---
Result<AppPasswordIssued> issued = await appPasswords.CreateAsync(user.Id, "iPhone", AppPasswordScope.Dav, ct);
Result<AppPasswordIssued> feed   = await appPasswords.CreateAsync(user.Id, "Outlook", AppPasswordScope.CalendarFeed(address), ct);
// a failure is an AppError with a code of AppPasswordErrorCodes: LimitReached = "app_password.limit_reached",
// NotFound = "app_password.not_found" (RevokeAsync)
Uri feedUrl = davLinks.FeedUrl(feed.Value);   // https://host/dav/feeds/<secret>.ics

// --- client ---
var client = new CalDavClient(httpClient, new CalDavClientOptions {
    Authorization = ct => ValueTask.FromResult(new AuthenticationHeaderValue("Bearer", token)),
});
CalDavResult<CalDavAccount> account = await client.DiscoverAsync(new Uri("https://caldav.example.com"), ct);
CalDavResult<CalDavSyncStep> step = await client.SyncAsync(account.Value.Calendars[0], previousState, ct);
// step.Value.Changed: IReadOnlyList<CalDavObject(Name, ETag, CalendarReadResult)>; .Removed; .State (string-serializable)
CalDavResult<CalDavWriteOutcome> put = await client.PutAsync(calendar, name, calendarObject, ifMatch: etag, ct);

// every client result has the entry shape of the format packages, with one error instead of a list
public sealed class CalDavResult<T> {
    public T? Value { get; }
    [MemberNotNullWhen(true, nameof(Value))] [MemberNotNullWhen(false, nameof(Error))] public bool HasValue { get; }
    public CalDavError? Error { get; }
}
public sealed class CalDavError { string Code; string Message; int? StatusCode; Uri? Url; long? Limit; }
public static class CalDavErrorCodes {
    Unauthorized = "caldav.unauthorized", NotFound = "caldav.not_found", Conflict = "caldav.conflict",
    ServerError = "caldav.server_error", InvalidResponse = "caldav.invalid_response", TooLarge = "caldav.too_large";
}
```

Options bags, limits named `Max…` as in the format packages:

- `WebDavOptions` (section `WebDav`): `Prefix` (`/dav`), `MaxBodyBytes` (1 MiB), `MaxXmlDepth` (32), `MaxXmlElements`
  (10,000), `MaxMultigetHrefs` (1,000), `JournalRetention` (90 days), `MaxJournalRowsPerCalendar` (10,000).
- `CalDavOptions` (section `CalDav`): `CalendarRead`, a `CalendarReadOptions` of `Elarion.Calendars` used for every
  calendar object a request or a response carries (its own defaults, not a copy of its limit names), `CalendarExpand`,
  a `CalendarExpandOptions` for the reports' expansion caps, and `FeedsEnabled`.
- `AppPasswordOptions` (section `AppPasswords`): `SecretPrefix` (`elap_`), `MaxPerUser` (10), `LastUseInterval`
  (15 minutes).
- `CalDavClientOptions`: `MaxResponseBytes` (32 MiB), `MaxXmlDepth` (32), `MaxObjectsPerStep` (5,000),
  `MultigetBatchSize` (100), `CalendarRead` (a `CalendarReadOptions`), timeouts.

Naming rule for the public types of `Elarion.CalDav` (ADR-0111): a name that starts with `Calendar` means the
`Elarion.Calendars` concept and lives there (`CalendarObject`, `CalendarComponentKinds`, `CalendarReadOptions`,
`CalendarWriter`); a type that exists only for DAV carries the `CalDav` prefix (`ICalDavSource<TSelf>`,
`AddElarionCalDavSource<TQuery>()`, `CalDavSourceListing`, `ICalDavPublisher`, `CalDavAddress`, `CalDavClient`,
`CalDavFilterEvaluator`). `PublishedCalendarInfo` keeps the glossary term *published calendar*. Internal types are
free of the rule.

## The store

Three tables, snake_case, mapped onto the application's context by the bundled generator; published names are
`elarion_dav_collections`, `elarion_dav_objects`, `elarion_dav_changes`.

| Table | Columns | Keys and indexes |
| --- | --- | --- |
| collections | `id` (uuid v7), `kind` (`calendar`), `source`, `key`, `sync_seq` bigint, `low_water` bigint | unique (`kind`, `source`, `key`) |
| objects | `collection_id`, `name`, `uid`, `etag`, `content` bytea, `content_type`, `range_start` timestamptz null, `range_end` timestamptz null, `size` | pk (`collection_id`, `name`); unique (`collection_id`, `uid`); index (`collection_id`, `range_start`, `range_end`) |
| changes | `collection_id`, `seq`, `name`, `op` (1 added, 2 changed, 3 removed) | pk (`collection_id`, `seq`) |

- **Publish**: compute bytes and ETag in memory; select the stored ETag; equal → return. Else `UPDATE collections SET
  sync_seq = sync_seq + 1 … RETURNING sync_seq` (row lock serializes concurrent publishers of one calendar), upsert the
  object, insert the change row, delete change rows of that collection older than the retention or beyond the row cap
  and set `low_water` to the smallest remaining seq minus one. A missing collection row is inserted with `ON CONFLICT DO
  NOTHING` first. All through the application's `DbContext` connection and transaction (the pattern of
  `Elarion.Blobs.PostgreSql`), so FR-005 holds without a second connection.
- **Replace**: stream the set, compute names and ETags, compare against a `(name, etag)` listing of the collection,
  apply the differences under one counter increment per changed object (the journal needs one seq per change for the
  sync answer's ordering; one row lock for the whole replace).
- **Timestamps**: the publisher never passes a clock value to the writer, so `DTSTAMP` comes from the item or its
  `LAST-MODIFIED` (ADR-0109); an item with neither fails the write, and publishing throws (FR-009), because a clock
  value would change the bytes, and so the ETag and the token, on every re-publish.
- **Object name**: the UID if it matches `[A-Za-z0-9._@-]{1,200}`, else `sha256(uid)` in base32; `.ics` appended. Stable
  across publishes, so re-publishing a UID never moves its URL.
- **Bounds**: `range_start`/`range_end` from `CalendarRecurrence.Expand` over a capped window (first and last
  occurrence; `range_end` null for an unbounded rule); floating times widened by ±14 hours; a component kind without a
  time (a `VTODO` with neither `DTSTART` nor `DUE`) stores nulls and is always a candidate.
- **Sync token**: `urn:elarion:sync:{collection id}:{seq}`. Parsing checks the collection id (foreign → invalid) and
  `seq >= low_water` (else invalid).
- **Seam**: `IDavCollectionStore` in `Elarion.WebDav` (list members with ETags, get objects by names, changes since
  seq, candidates by range, publish, remove, replace). The EF store is its only implementation now; tests run it on
  PostgreSQL through Testcontainers, never on an in-memory provider.

## The WebDAV dispatcher and host

- `Elarion.WebDav` exposes `DavDispatcher.HandleAsync(DavRequest, IServiceProvider, CancellationToken) →
  DavResponse`: a method, a path below the prefix, headers it needs (`Depth`, `If-Match`, `If-None-Match`, `Brief`,
  `Prefer`), and the body as a `Stream`. The response writes itself to a `Stream`. No ASP.NET type crosses the seam,
  so dispatcher tests run without a server.
- Path resolution: `principals/{user}/`, `calendars/{user}/`, `calendars/{user}/{source}.{key}/`,
  `…/{name}.ics`, `feeds/{secret}.ics`. `{user}` must equal `ICurrentUser.Id` (else 404, FR-012). Protocol modules
  (CalDAV now, CardDAV later) register the path segments and properties they own through
  `IDavProtocol` implementations; the host does not know them.
- Calendar sources are resolved once per request: every registered `ICalDavSource<T>` handler is invoked through
  `HandlerInvoker` in the request's dispatch scope (FR-011); `NotFound`, `Forbidden` and `Unauthorized` from a source
  mean "no calendars from this source", any other error answers `500` through the framework's error answer. The
  result is a request-scoped map `address → PublishedCalendarInfo`.
- XML: `XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersInDocument` from `MaxBodyBytes`,
  a depth counter against `MaxXmlDepth` and an element counter against `MaxXmlElements`; parsed into small request records (`PropFindRequest`, `SyncCollectionRequest`,
  `CalendarQueryRequest`, `MultigetRequest`, `FreeBusyRequest`). Unknown elements are ignored where the RFCs allow it.
  Responses are written with `XmlWriter` (async, UTF-8, no BOM), with namespace prefixes `D`, `C`, `CS`, `ICAL`
  fixed so diffs in tests stay readable. `calendar-data` is written as text content of the object's bytes.
- Properties are a table per resource kind: name → writer, so `allprop`, `propname` and `prop` share one path and an
  unknown property lands in the `404` propstat (FR-017).
- `sync-collection`: `sync-level` 1 only (`infinity` → `403` with the `sync-traversal-supported` precondition of
  RFC 6578, since calendars have no child collections); a `limit` lower than the change count answers the first N with a token at the Nth change and
  `507` on the request-URI, which DAVx5 and Thunderbird continue from (the research note's Nextcloud finding shows why
  silently cutting is wrong).
- `calendar-query`: candidates by range from the store, then each candidate parsed with `Elarion.Calendars` and
  matched by the filter tree in `Elarion.CalDav` (`CalDavFilterEvaluator`): `comp-filter`, `prop-filter`,
  `param-filter`, `text-match` with `i;ascii-casemap` and `i;unicode-casemap` (default) and `negate-condition`,
  `is-not-defined`, `time-range` per the RFC 4791 §9.9 table using occurrences from `CalendarRecurrence.Expand`.
  `expand` and `limit-recurrence-set` rewrite the component tree (`CalendarComponent` is mutable with public
  constructors and `Clone()`); partial `calendar-data` prunes a clone. Both write through `CalendarWriter`. The
  `time-range` rows of the RFC table pick their rule from `CalendarOccurrence.EndKind` (`Stated`, `Duration`,
  `DefaultDay`, `DefaultZero`); recurring `VTODO`s expand with `DUE` as the end; the `VALARM` test uses
  `CalendarAlarm.GetTriggerTimes(occurrence)`; floating times use `CalendarExpandOptions.FloatingTimeZone` set to the
  calendar's time zone. A request with `CalDAV-Timezones: F` (RFC 7809) is answered with
  `CalendarWriteOptions.IncludeTimeZones = false`.
- `free-busy-query`: occurrences of every `VEVENT` without `TRANSP:TRANSPARENT` and not `CANCELLED`, merged into one
  `VFREEBUSY` built on the tree.
- Host (`MapElarionDav`): `MapMethods` on one catch-all route below the prefix for `OPTIONS`, `GET`, `HEAD`,
  `PROPFIND`, `REPORT`, and the write methods (answered `403` by the dispatcher); `/.well-known/caldav` mapped at the
  root (not inside the group) redirecting `301` to the prefix for every method (FR-015). The group requires the
  schemes named in `WebDavHostOptions.AuthenticationSchemes` (default: the app-password scheme), calls `SeedScope` after
  authentication so `ICurrentUser` holds the DAV principal (the friction the consuming application reported), and
  carries an endpoint marker that `UseElarionCrossOriginProtection` skips (FR-036). The feed path is anonymous at the
  ASP.NET level up to the scheme: the scheme reads the secret from the path itself.
- Responses stream: `multistatus` is written forward into the response body; objects come from the store as
  `content` bytes per row, never the whole calendar at once (feeds stream object by object, deduplicating
  `VTIMEZONE`s by `TZID`, which needs each object's tree parsed once: the feed is the one place that parses every
  object; acceptable at the tier and cached by the HTTP `ETag` of the feed, which is the collection's seq).

## App passwords

- Secret: prefix + 32 bytes `RandomNumberGenerator`, base64url without padding. Stored: SHA-256 over the UTF-8 bytes of
  the whole secret as the client sends it, prefix included, as standard Base64 with padding, in a unique index; this
  exact encoding is a compatibility contract (see "Adopting existing app passwords"); also `id` (uuid v7), `user_id` (string, as `ICurrentUser.Id`), `name`, `scope`, `created_at`,
  `last_used_at`. Revoke is a hard delete scoped to the user.
- `IAppPasswords.VerifyAsync(secret, requiredScope)` → `AppPasswordMatch?` (id, user, scope); writes `last_used_at`
  only when older than the interval.
- Scheme (`AppPasswordAuthenticationHandler`, hand-written over `AuthenticationHandler<AppPasswordAuthenticationOptions>`,
  no outside package): Basic → split at the first colon; feed paths → secret from the path. Then the events, in order:
  - `OnVerifyCredential(AppPasswordCredentialContext)` → `AppPasswordMatch?` (user id, scope, credential id). Default:
    `IAppPasswords.VerifyAsync(secret, requiredScope)` (scope `dav` for Basic, `calendar-feed:{source}.{key}` for a
    feed). A replacement returns its own match; `IAppPasswordStore` is then not resolved, so the EF package is optional.
  - `OnValidateUserName(AppPasswordUserNameContext)` → `bool`. Default: `true` (not checked). Not run for feeds.
  - `OnCreatePrincipal(AppPasswordPrincipalContext)` → `ClaimsPrincipal?`; `null` refuses (deactivated, deleted). No
    default: startup fails with a message naming `AddElarionIdentityAppPasswordPrincipals<TUser>()` when neither is set.
    The Identity default uses `UserManager<TUser>` and `IUserClaimsPrincipalFactory<TUser>`; applications on proxy
    identity or OIDC set the delegate over their own user service.
  - `OnAuthenticated(AppPasswordAuthenticatedContext)` → `Task`, for audit and metrics.
  Every context derives from one base with `HttpContext`, `Services` (the request scope's provider), `UserName`
  (Basic only), `Scheme` and `CancellationToken`; all events are `Func<TContext, ValueTask<…>>` properties on an
  `AppPasswordAuthenticationEvents` object, so an application can also subclass it and register the type, as with
  `JwtBearerEvents`. The scheme adds the claims `elarion:app_password_id` and `elarion:app_password_scope` to the
  principal, and the dispatcher checks a feed's scope against the requested calendar. Challenge `401` with `Basic realm="{Realm}", charset="UTF-8"`; forbidden `403` without a challenge.
  Whether it compiles warning-free under `PublishAot` is checked in slice 1 (the research note could not verify it).

As built in slice 1, where it settles or departs from the above:

- **AOT**: `Elarion.AppPasswords` and `Elarion.AppPasswords.AspNetCore` are `IsAotCompatible` and build without
  warnings; a minimal web host using the scheme, the Identity principal factory and the configuration binding published
  with `PublishAot` on `net11.0` with no trimming or AOT warning and answered as expected.
- **Feed scope without `CalDavAddress`**: `Elarion.CalDav` does not exist yet, so `AppPasswordScope.CalendarFeed(string
  source, string key)` takes the two strings; `Elarion.CalDav` adds an overload or extension over `CalDavAddress`.
  `AppPasswordScope` also has `Parse`/`TryParse`, `Value`, `Kind` (`Dav`, `CalendarFeed`, `Other` for a later version's
  scope) and `CalendarSource`/`CalendarKey`.
- **Feed verification by kind**: a feed URL holds only the token, so the scheme cannot know the calendar before the
  token is checked. `IAppPasswords` has a second `VerifyAsync(secret, AppPasswordScopeKind)`; the default
  `OnVerifyCredential` checks `Dav` for Basic and `CalendarFeed` for a feed, the scheme refuses a match of the other
  kind whatever the verifier, and the dispatcher compares the claimed scope with the requested calendar (slice 5).
- **Feed path**: `AppPasswordAuthenticationOptions.FeedTokenReader` (`Func<HttpRequest, string?>`), default
  `AppPasswordFeedPaths.CreateReader("/dav")` for `{prefix}/feeds/{token}.ics`; the DAV host sets it to its prefix. A
  challenge on a feed path answers `404` without `WWW-Authenticate` (FR-026).
- **Principal default**: `IAppPasswordPrincipalFactory`, registered by `AddElarionIdentityAppPasswordPrincipals<TUser>()`,
  is used when `OnCreatePrincipal` is not set; an options validator run on start fails the host when neither (nor an
  `EventsType`) is there. `OnAuthenticated` returns `ValueTask` like the other events.
- **Dependencies**: `Elarion.AppPasswords.AspNetCore` references `Elarion.AppPasswords` and the ASP.NET Core shared
  framework only, not `Elarion.AspNetCore`, which it does not use; Identity's `UserManager` is in the shared framework.
- **Store**: the EF store is a singleton that opens its own scope per call (the device-identity pattern), so creating a
  password does not join or save the caller's unit of work; the cap is a count and an insert in one transaction under a
  PostgreSQL advisory lock on the user. The generated set is `ElarionAppPasswords`, so an adopting application can keep
  its own `AppPasswords` set during migration. No store is registered by `AddElarionAppPasswords`;
  `AddElarionInMemoryAppPasswordStore()` exists for tests.

## Adopting existing app passwords

An application that already issues its own Basic credentials for DAV clients (as the consuming application whose
CardDAV surface informed ADR-0111 does) moves to Elarion's scheme without asking anyone to create a new password and
re-enter it on every device. It has two paths, and the capability page documents both:

1. **Keep its own store.** It registers no `Elarion.AppPasswords.EntityFrameworkCore` and sets `OnVerifyCredential` to
   its existing check, `OnValidateUserName` to its user-name rule (for example "the e-mail of the owning account,
   compared case-insensitively"), and `OnCreatePrincipal` to its principal construction, including any account checks
   of its own (active, within its validity period). Only the handler class changes; its table, management handlers and
   UI stay. This is the step that needs no data migration.
2. **Move the rows into Elarion's store.** Possible without the secrets when the old store kept SHA-256 over the UTF-8
   secret as standard Base64, which is the encoding above: a migration step of the application copies each row into
   the app-password table with `scope = 'dav'`, its user id as a string, name, creation and last-use times, and the
   hash unchanged; then it drops its own table and handlers and uses `IAppPasswords` for management. The hash covers
   the whole secret as typed, so secrets with an older prefix keep verifying, and `AppPasswordOptions.SecretPrefix`
   (set to the application's prefix) affects only new ones. An old store with another hash encoding stays on path 1,
   or its users re-create their passwords.

The cap per user, the last-use interval and the hard-delete revocation default to what such stores already do (10,
15 minutes, delete), so neither path changes behaviour a user notices. What Elarion cannot carry over is a user-name
rule: path 2 still needs `OnValidateUserName` when the application wants one.

## The client

- `CalDavClient` over an injected `HttpClient`; requests built with the same XML writer, responses read with the same
  hardened reader and size limits (FR-043).
- Discovery: `PROPFIND` `current-user-principal` on the base URL; on `404`/`405` or no answer, `/.well-known/caldav`
  following up to 5 redirects; then `calendar-home-set` from the principal (depth 0; never depth 1 on the principal,
  which iCloud answers with `400`), then depth 1 on the home.
- Sync step, with state `{ syncToken?, ctag?, etags: name → etag }` serialized as compact JSON through a source-
  generated context (base64url string): with a token, `sync-collection`; on `403`/`409`/`412` with
  `valid-sync-token`, or `507` truncation handled by continuing, as the research note describes per server; without
  token support, `getctag` then a depth-1 `PROPFIND` of ETags and a diff. Changed names fetched by
  `calendar-multiget` in batches; servers known to return deleted objects in multiget (Zimbra) are handled by treating a
  `404` propstat as a removal.
- Results: `CalDavResult<CalDavSyncStep>`; on success the step holds the objects with their `CalendarReadResult`
  (errors coded `calendar.*` by the reader) and the new state; otherwise `Error` holds a `CalDavError` with its code
  (`caldav.unauthorized`, `caldav.not_found`, `caldav.server_error` with the status, `caldav.invalid_response`,
  `caldav.too_large` with the limit), its English message and its data, and the caller keeps its previous state.
- Writes: `PUT` with `If-Match` or `If-None-Match: *`, `DELETE` with `If-Match`; `412` → `caldav.conflict`; on success
  `CalDavWriteOutcome` holds the new ETag when the server returns one, else `null` (RFC 4791 §5.3.4), in which case
  the next sync step fetches it.

## Tests

The seams: `DavDispatcher` in-process (request records in, bytes out), the EF store on Testcontainers PostgreSQL,
the ASP.NET host through `WebApplicationFactory`, the client against the host of this very package (round trip) and
against recorded responses of iCloud, Google, Nextcloud, Fastmail and Zimbra quirks.

- One test per acceptance scenario of the spec, named after it.
- **Client replays**: the discovery and sync sequences of iOS, macOS, Thunderbird, DAVx5 and Evolution, transcribed as
  request lists from the research note's sources (their code paths, sabre/dav's client notes), each asserted against
  the host: the status codes, the properties each client reads, the read-only privilege set.
- **Conformance oracle**: Shiny.AspNet.CalDav as a test-only reference for the XML shapes of the reports (no runtime
  dependency), and Apple's CalDAVTester scripts (`well-known`, `options`, `propfind`, `sync-report`, `ctag`) run against
  a sample host in a separate, manually triggered CI job, since they need their own Python tooling.
- **Sync properties**: a randomized test publishes, changes, removes and replaces objects in random order while a
  simulated client syncs at random points, with pruning in between; the client's copy must always equal the store
  after its last sync (FR-006, FR-007, FR-020).
- **Concurrency**: two publishers of one calendar in parallel transactions; tokens strictly increase and no change is
  lost; a rolled-back publish leaves no journal row.
- **Adoption**: a secret hashed by an independent SHA-256/UTF-8/Base64 implementation and inserted as a row verifies
  through the default `OnVerifyCredential`, with and without a legacy prefix; a replaced `OnVerifyCredential` never
  resolves `IAppPasswordStore`.
- **Hostile input**: DTD, entity expansion, a 100 MB body, depth 10,000, a `calendar-query` whose expansion exceeds the
  cap.
- **AOT**: the NativeAOT sample publishes in CI (`ci: publish the EdgeTelemetry sample with NativeAOT` exists), with
  `IsAotCompatible` on the three protocol packages.
- **Benchmarks** in `tests/Elarion.Benchmarks`: an unchanged `sync-collection` on 1,000 objects (SC-004) and a
  publish of an unchanged object (no write).

## Documentation

- `docs/capabilities/calendars/caldav.mdx` (publishing, sources, feeds, the host, client), `docs/capabilities/app-
  passwords.mdx` (with a section on adopting existing app passwords, both paths above), both in their `meta.json`; `docs/reference/packages.mdx` rows for the seven packages; diagnostics
  and configuration reference tables; `CHANGELOG.md`; the glossary terms (calendar source, published calendar, app
  password, feed token) land with this design. The authorization page's sentence that Elarion does not issue
  credentials gets the two exceptions.

## Slices

Each slice is one pull request, green on its own, in this order:

1. `Elarion.AppPasswords` + EF store + ASP.NET scheme, with the Identity principal factory (FR-028–FR-036). Useful on
   its own; settles the AOT question of the authentication handler.
2. `Elarion.WebDav` core: XML codec, dispatcher, properties, principals and home, `OPTIONS`/`PROPFIND`, the store seam
   and the EF store with journal and `sync-collection` (FR-014–FR-018, FR-020, FR-021, FR-023–FR-025), tested with a
   test-only collection kind.
3. `Elarion.CalDav` server: sources, publisher, calendar properties, multiget, `calendar-query`, `free-busy-query`,
   `expand` (FR-004–FR-013, FR-019, FR-022). Needs `Elarion.Calendars`.
4. `Elarion.WebDav.AspNetCore` host and well-known, then the client replays and the sample (FR-015, FR-016, FR-033,
   FR-036; SC-001, SC-002, SC-005).
5. Feeds (FR-026, FR-027).
6. `CalDavClient` (FR-037–FR-043).

## Risks

- **Clients we cannot run in CI.** iOS and macOS behaviour rests on documentation and bug trackers; SC-001 is a manual
  check before release, and the replays encode what we know.
- **`Elarion.Calendars` timing.** Slice 3 depends on its writer, recurrence engine and tree building; slices 1 and 2
  do not.
- **Per-viewer calendars multiply rows.** At 2,000 users with 500 objects each the store holds a million objects;
  within one PostgreSQL, but the sample should publish per-viewer calendars to keep the cost visible.
- **Store drift.** A handler that forgets to publish leaves a stale calendar. The capability page shows the pattern of
  publishing from a domain event consumer in the same transaction, and `ReplaceAsync` as the repair.
