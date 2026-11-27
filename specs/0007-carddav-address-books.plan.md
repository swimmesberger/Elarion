---
type: Plan
title: "Plan 0007: CardDAV address books"
description: "How the CardDAV support of spec 0007 is built: the Elarion.CardDav project and its public surface, what moves into the WebDAV core so both protocols share it, the address-book reports and vCard version conversion, tests and slices."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T10:30:00Z }
status: stable
---
# Plan 0007: CardDAV address books

How [spec 0007](0007-carddav-address-books.md) is built. It follows [plan 0006](0006-caldav-calendars.plan.md),
including its "As built" notes, wherever CardDAV has the shape CalDAV has; this plan names only what differs or is new.
The decisions behind it are [ADR-0111](../docs/decisions/0111-caldav.md) and
[ADR-0113](../docs/decisions/0113-carddav-address-books.md); the contact model is `Elarion.Contacts`
([ADR-0110](../docs/decisions/0110-vcard-contacts.md)). The evidence for every client rule below is the research note
[CardDAV clients](../docs/research/2026-11-27-carddav-clients.md).

## Projects

| Project | Notes |
| --- | --- |
| `src/Elarion.CardDav` | New. `IsAotCompatible`. References `Elarion.WebDav`, `Elarion.Contacts`, `Elarion.Abstractions`. Address-book sources, `ICardDavPublisher`, address-book properties, `addressbook-multiget`, `addressbook-query`. |
| `src/Elarion.WebDav` | Gains the protocol-neutral parts both protocols need (below) and `InternalsVisibleTo Elarion.CardDav`. |
| `src/Elarion.CalDav` | Moves onto the shared parts; no behaviour change, its tests stay green unchanged. |
| `src/Elarion.WebDav.AspNetCore`, `src/Elarion.WebDav.EntityFrameworkCore`, the app-password packages | Unchanged: the host maps `/.well-known/{protocol}` per registered protocol, the store keys collections by kind, the scheme's `dav` scope is protocol-neutral. |
| `tests/Elarion.Tests/CardDav` | One folder, as `CalDav/`. |

Runtime projects build for `net10.0` and `net11.0`. No outside package.

## Public surface

```csharp
// --- Elarion.CardDav: declaring a source (in a module) ---
public sealed record CustomerAddressBooks : ICardDavSource<CustomerAddressBooks> {
    public static string SourceName => "customers";
}

// No [Handler]: called in process through the module's pipeline, no JSON-RPC operation (as calendar sources).
[RequirePermission("customers", Verbs.Read)]
public sealed class ListCustomerAddressBooks : IHandler<CustomerAddressBooks, Result<CardDavSourceListing>> {
    public ValueTask<Result<CardDavSourceListing>> HandleAsync(CustomerAddressBooks query, CancellationToken ct) =>
        ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([
            new PublishedAddressBookInfo(Key: "all", DisplayName: "Customers") { Description = "Every active customer" },
        ])));
}

// module ConfigureServices
services.AddElarionCardDavSource<CustomerAddressBooks>();

// --- publishing, inside a command handler ---
await addressBooks.PublishAsync(new CardDavAddress("customers", "all"), ToContact(customer), ct);   // Contact.Uid required
// also: RemoveAsync(address, uid, ct); ReplaceAsync(address, IAsyncEnumerable<Contact> | IEnumerable<Contact>, ct)
// results as ICalDavPublisher: bool changed, DavReplaceOutcome

// --- host ---
builder.Services.AddElarionCardDav(builder.Configuration.GetSection(CardDavOptions.SectionName));
app.MapElarionDav();   // unchanged; now also /dav/addressbooks/… and /.well-known/carddav
```

- `CardDavOptions` (section `CardDav`): `ContactRead`, a `ContactReadOptions` of `Elarion.Contacts` used when a stored
  card is read back (conversion to 4.0, partial `address-data`, `addressbook-query`). No limits of its own.
- Naming rule of ADR-0111, applied to contacts: a public type whose name starts with `Contact` is `Elarion.Contacts`'
  (`Contact`, `ContactReadOptions`, `ContactWriter`); a type that exists only for DAV carries `CardDav`
  (`ICardDavSource<TSelf>`, `CardDavSourceListing`, `ICardDavPublisher`, `CardDavAddress`, `CardDavOptions`,
  `AddElarionCardDav`, `AddElarionCardDavSource<TQuery>()`). `PublishedAddressBookInfo` keeps the glossary term
  *published address book*.
- `PublishedAddressBookInfo(string Key, string DisplayName)` with `Description`. No color: no client reads one for
  address books (research note).

## What moves into the WebDAV core

CalDAV's source machinery is protocol-neutral except for its types. Rather than copy ~200 lines, slice 1 moves it into
`Elarion.WebDav` as internals shared through `InternalsVisibleTo`, generic over the listing:

- `DavSourceRegistration<TListing>` (name, query type, handler service type, invoke delegate), `DavSourceRegistry<TListing>`
  (duplicate names fail), `DavSourceHandlerValidator<TOptions, TListing>` (the start check), the segment rule
  (`DavCollectionAddress.IsValidSegment`, which `CalDavAddress.IsValidSegment` then calls), and
  `DavSources.InvokeAsync`, which turns a source's `Result` into "no collections" for `NotFound`, `Forbidden` and
  `Unauthorized` and throws otherwise.
- `DavObjectNames.NameOf(uid, extension)`: the stable member name (the UID when it is 1 to 200 of `A–Z a–z 0–9 . _ @ -`,
  else the base32 of its SHA-256), now with the extension as a parameter (`.ics`, `.vcf`).
- The `text-match` collations (`i;ascii-casemap`, `i;unicode-casemap`, `i;octet`) and match types, now in
  `DavTextMatch`, used by both filter evaluators. CalDAV keeps its default collation (`i;ascii-casemap`, RFC 4791
  §9.7.5); CardDAV's is `i;unicode-casemap` (RFC 6352 §10.5.4) and its default match type `contains`.
- The `limit`/`507` truncation writer CalDAV's `sync-collection` uses, reused by `addressbook-query`.

Public types of `Elarion.CalDav` do not change. Each moved piece keeps its CalDAV tests; the slice is green before
CardDAV starts.

## The protocol

`CardDavProtocol : IDavProtocol`:

- `CollectionKind` `addressbook`, `HomeSegment` `addressbooks`, `WellKnownName` `carddav`, `ComplianceClasses`
  `[addressbook]`, `CollectionResourceTypes` `[CARD:addressbook]`, `CollectionReports` `[addressbook-multiget,
  addressbook-query]`. `DavNames` gains `CardDav` (`urn:ietf:params:xml:ns:carddav`) with prefix `CARD`.
- Principal property `CARD:addressbook-home-set`. Not `CARD:principal-address`: no client needs it (research note), and
  the principal has no card.
- Collection properties: `CARD:addressbook-description` (when set), `CARD:supported-address-data` (`text/vcard` 3.0
  and 4.0), `CARD:supported-collation-set`, `CARD:max-resource-size` is not written (it limits `PUT`, and there is none).
  `displayname`, `resourcetype`, `sync-token`, `getctag`, privileges and `supported-report-set` come from the core.
- Member property `CARD:address-data`.
- `GetCollectionsAsync`: every registered source through `DavSources.InvokeAsync` in the request's scope, keys
  validated, `CardDavCollection : DavCollection` holding `CardDavAddress` and `PublishedAddressBookInfo`.
- `HandleOtherAsync`: `null`; no feeds.
- The store needs nothing new: collections are keyed by kind, so `addressbook` and `calendar` sources of one name are
  distinct rows with distinct ids and tokens (FR-015). `RangeStart`/`RangeEnd` stay null for contacts.

## Publishing

`CardDavPublisher : ICardDavPublisher`, scoped over `IDavCollectionStore`, as `CalDavPublisher`:

- Guard: source registered (else `InvalidOperationException` naming `AddElarionCardDavSource`), `Contact.Uid` not null
  or empty and `Contact.DisplayName` not empty (else `ArgumentException`), because the 3.0 writer then writes an empty
  `FN`, which Apple drops (FR-010, FR-031); `N` is always written in 3.0.
- Bytes: `ContactWriter` with `ContactWriteOptions { Version = V3_0 }`, one card. The writer is deterministic and writes
  no clock value (`REV` is written only when the contact carries it), so equal contacts give equal bytes and FR-007
  needs nothing more. A `ContactWriteException` propagates (FR-010).
- `DavObjectContent { Name = NameOf(uid, ".vcf"), Uid, Content, ContentType = "text/vcard; charset=utf-8", ETag =
  DavETag.Compute(bytes) }`.
- Replace: as CalDAV, plus a duplicate UID in the set throws before the store applies anything (the store's unique
  `(collection_id, uid)` index would otherwise fail mid-way).

## Reports

- `addressbook-multiget`: hrefs below the collection, at most `MaxMultigetHrefs` (`WebDavOptions`), members by name
  from the store; an unknown href answers `404` in its own `response`. Properties through the shared property tables.
- `addressbook-query`: no index; every member of the collection is read from the store in pages and parsed with
  `ContactReader` (`CardDavOptions.ContactRead`), then matched by `CardDavFilterEvaluator` against the entry's property
  list (not the `Contact` model, so `X-` properties and groups are searchable):
  - `prop-filter name` matches a property name case-insensitively, ignoring the group (`item1.EMAIL` is `EMAIL`).
  - `test` on `filter` and `prop-filter`: `anyof` (default) or `allof`.
  - `is-not-defined` on a `prop-filter` or a `param-filter`.
  - `text-match` on the property's unescaped text value, or on any value of a `param-filter`'s parameter
    (`TYPE=work,pref` holds two); `match-type` `equals`, `contains`, `starts-with`, `ends-with`; `negate-condition`.
    Structured values (`N`, `ADR`) match against the whole value with `;` separators, as sabre/vobject does.
  - Collations from `DavTextMatch`; unknown → `403` with `CARD:supported-collation`; an unknown match type, a
    `prop-filter` without a name, or a filter element of another namespace → `403` with `CARD:supported-filter`.
  - `limit/nresults`: the first N matches in member-name order, then a `507` response for the request URL
    (FR-024).
  - No `filter` element matches every card (Evolution's `list_existing_sync`, sabre does the same). `Depth: 0` on the
    address book is read as `1` for this report, unlike `calendar-query`, because Evolution sends `Depth: 0` there and
    expects the members; a missing `Depth` reads as `1`.
  The cost is a parse of every card per query; at the tier (a few thousand contacts per address book) that is
  acceptable, and no client found by the research note queries on every sync. A benchmark records it.
- `address-data` (`CardDavAddressData.RenderAsync`):
  - no attributes, or `content-type="text/vcard"` with `version="3.0"`: the stored bytes, unless properties are named.
  - `version="4.0"`: the stored card read with `ContactReader`, mapped to a `Contact` and written with
    `ContactWriter` as 4.0. The `Contact` keeps every property it does not model (`AdditionalProperties`), so nothing
    is lost that 3.0 carried; the ETag stays the stored one (FR-027), which is what sabre/dav does when it converts.
  - `prop name="…"` (with `novalue`): the entry's property list filtered to the named properties plus `VERSION`, `UID`
    and `FN`, written with `ContactWriter.AddAsync(IReadOnlyList<ContactProperty>)` in the requested version; for 4.0 the
    filtering runs on `Contact.ToProperties(V4_0)`.
  - Another content type or version: `403` with `CARD:supported-address-data` in that member's `propstat` inside a
    report, and on the request in a `PROPFIND`. `text/vcard; charset=utf-8` as content type is accepted.
- `sync-collection`: the core's, unchanged; `address-data` in its `prop` is rendered as above.
- `GET`: the core answers the stored bytes; `CardDavProtocol` gains the hook to answer 4.0 when `Accept` ranks
  `text/vcard; version=4.0` above plain `text/vcard` (FR-028, sabre's negotiation). The core's `GET` asks the member's
  protocol for a representation through a new optional `IDavProtocol` member with a default implementation that
  returns the stored bytes, so CalDAV is unaffected.

## The host

Nothing changes in `Elarion.WebDav.AspNetCore`: `MapElarionDav` already maps `/.well-known/{WellKnownName}` for every
registered `IDavProtocol` and routes every path below the prefix to the dispatcher, which finds the protocol by its
home segment. The dispatcher's `DAV` header is the union of the protocols' classes (`1, 3, calendar-access,
addressbook` with both) on every DAV URL, the principal included, which DAVx5 checks before it accepts a principal
for CardDAV; the principal's properties are the union of their principal properties, so DAVx5 and Evolution, which
discover both services from one principal, find both homes (FR-016, FR-018). iOS, macOS and Thunderbird add contacts
as an account of their own, with the same host, user and app password. The slice verifies all of it with a host that
registers both protocols.

What Apple reads and does not get: `CS:me-card`, `max-image-size`, `push-transports`, `bulk-requests`,
`directory-gateway` answer `404` in the propstat, which iOS and macOS tolerate (research note).

The member `GET` already answers the stored content type; `text/vcard; charset=utf-8` is what the publisher stores.

## As built

Where the implementation settles or departs from the above:

- **Shared source machinery**: `DavSources.Register`/`AddRegistry`/`InvokeAsync`/`CheckKey`, `DavSourceRegistry<TListing>`
  and `DavSourceHandlerValidator<TListing, TOptions>` in `Elarion.WebDav`, with a `DavSourceKind` (noun and
  registration method) per protocol for the messages; the kind is a `DavSourceKindOf<TListing>` service so the
  validator registers by type and `AddElarion…Dav` stays safe to call repeatedly. The caller makes its options
  validate on start, since a generic `ValidateOnStart` needs a trimming annotation the shared helper cannot carry.
- **`GET` representations**: `IDavProtocol.GetRepresentationAsync` is a default interface member returning `null`
  (the stored bytes), with a public `DavRepresentation(Content, ContentType)`; CardDAV answers 4.0 with
  `text/vcard; charset=utf-8; version=4.0`. No `Vary: Accept`: DAV answers are private and not cached by intermediaries.
- **An unsupported `address-data`** is refused with `403 supported-address-data` by `addressbook-multiget` and
  `addressbook-query` before any member is written; in a `PROPFIND` or a `sync-collection`, which the core answers
  without asking the protocol, it falls back to the stored 3.0 card rather than failing the whole answer.
- **Replace holds the set in memory** before the store sees it, to refuse a duplicate UID before anything is applied;
  at the tier an address book is a few thousand cards.
- **`CardDavFilterEvaluator` is internal**; `CalDavFilterEvaluator` stays public. A filter element other than
  `prop-filter`, an unknown `test`, an unknown match type or a nameless filter answers `supported-filter`.
- **Benchmarks**: `CardDavBenchmarks` (unchanged sync and a query over 1,000 contacts, through the dispatcher on the
  in-memory store). No CalDAV benchmark existed to extend.
- **Not done**: the NativeAOT publish of a host with both protocols (SC-004) was not run in this change; the package is
  `IsAotCompatible` and builds without trimming or AOT warnings. The real devices (SC-001) are a manual check.

## Tests

Seams as in plan 0006: the dispatcher in process through `DavTestHarness` with the in-memory store, the EF store on
Testcontainers PostgreSQL, the host on Kestrel.

- One test per acceptance scenario of spec 0007, named after it, in `CardDavPublishingTests`, `CardDavSourceTests`,
  `CardDavSourceAuthorizationTests` (the permission is enforced by the module's decorator chain, the source without a
  handler fails the start), `CardDavHostTests`, `CardDavReportTests` and `PostgreSqlCardDavTests` (commit and rollback
  visibility, the 300 → 299 replace, both kinds under one source name).
- **Client replays** (`CardDavClientReplayTests`): the discovery and sync sequences of iOS, macOS Contacts,
  Thunderbird, DAVx5 and Evolution, transcribed from the research note, run against a Kestrel host that registers
  CalDAV and CardDAV, asserting status codes, the properties each client reads, the read-only privilege set, the
  version each asks for, and that DAVx5 and macOS find both homes from one principal.
- **Filter evaluator**: a table of `prop-filter`/`param-filter`/`text-match` cases against cards from the vCard
  research note's producers (Apple groups and labels, Google's escaped colons, Android's quoted-printable 2.1 read back).
- **Version conversion**: every fixture card published, fetched as 3.0 and 4.0, read back with `ContactReader`; both
  map to equal `Contact`s, and the ETag is the same.
- **Sync properties**: the randomized publish/replace/remove/sync test of plan 0006 run with contacts.
- **Hostile input**: a query with 10,000 `prop-filter`s (the core's element limit), a `text-match` longer than the body
  limit, a multiget over `MaxMultigetHrefs`.
- **AOT**: `IsAotCompatible` with warnings as errors; a minimal host with both protocols published with `PublishAot`
  on `net11.0` once by hand, as slice 1 of plan 0006 did (SC-004).
- **Benchmarks**: an unchanged `sync-collection` on 1,000 contacts (SC-003) and an `addressbook-query` over 1,000.

## Documentation

`docs/capabilities/carddav.mdx` (publishing, sources, the host, client notes, what is out of scope), added to
`docs/capabilities/meta.json` after `contacts`; rows in `docs/reference/packages.mdx` and the configuration reference
(`CardDav` section); `caldav.mdx` and `app-passwords.mdx` link it; the `elarion` skill names the package;
`CHANGELOG.md`; `GLOSSARY.md` gains *address-book source* and *published address book*; `docs/log.md`.

## Slices

One commit each, green on its own:

1. Refactor: the shared source machinery, object names, text-match collations and truncation move into
   `Elarion.WebDav`; CalDAV uses them (no behaviour change).
2. `Elarion.CardDav`: project, options, sources and registration with the start check, the publisher, the protocol with
   its properties (FR-001–FR-020, FR-029, FR-030).
3. `addressbook-multiget`, `address-data` with version conversion and partial cards (FR-021, FR-025–FR-028).
4. `addressbook-query` and the filter evaluator (FR-022–FR-024).
5. Host tests with both protocols, client replays, PostgreSQL tests and benchmarks (FR-016–FR-018; SC-002, SC-003).
6. Documentation, glossary, changelog, skill.

## Risks

- **Apple and read-only address books.** iOS may show an alert or copy an edited contact into the default account,
  and an account with only read-only address books cannot be iOS's default contacts account (research note). Writes
  answer `403` with `need-privileges`, never `401`, which Apple reads as a bad password and stops syncing. The
  capability page says what the person sees.
- **Apple and several address books.** macOS downloads one address book per account, unpredictably chosen, and iOS at
  best merges them (research note). ADR-0113 decides to document it: an application that serves Apple users publishes
  one address book per user, for example a source that returns one "Contacts" book holding everything the user may
  see. Scoping an app password to one address book is the revisable alternative.
- **Photo size.** iCloud caps images at 224 KiB and DAVx5 resizes photos over 1 MB; the capability page recommends
  photos under 224 KiB. The publisher does not resize.
- **Query cost.** `addressbook-query` parses every card; if a client turns out to query on every sync, the store gains
  an index of searchable values later, behind the same seam.
