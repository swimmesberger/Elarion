---
type: Specification
title: "Spec 0007: CardDAV address books"
description: "What Elarion's CardDAV support must do: publish an application's contacts read-only as address books to iOS, macOS, Thunderbird, DAVx5 and Evolution over the WebDAV core and the app passwords of spec 0006, with exact sync, under NativeAOT."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T10:00:00Z }
status: stable
---
# Spec 0007: CardDAV address books

This spec describes the new opt-in package `Elarion.CardDav`. It builds on the WebDAV core, its store and host and the
app passwords of [spec 0006](0006-caldav-calendars.md), designed in [ADR-0111](../docs/decisions/0111-caldav.md) so
that CardDAV could follow; the decisions CardDAV adds are in [ADR-0113](../docs/decisions/0113-carddav-address-books.md).
The contact model, reader and writer are `Elarion.Contacts` ([ADR-0110](../docs/decisions/0110-vcard-contacts.md),
spec 0005).

## Intent

A developer lets people see the application's contacts (colleagues, customers, members, suppliers) in the contacts app
they already use, next to their own. A module publishes contacts in the handler that changes its data and declares an
address-book source, an ordinary handler that returns the address books the current user may see, so authorization,
resource rules, feature gates and module enablement decide what a contacts app shows exactly as they decide what the
application shows. A person adds an account with the host name, their e-mail and an app password in iOS, macOS,
Thunderbird, DAVx5 or Evolution, and sees the address books, read-only, kept in sync within the client's polling
interval; an application that serves calendars too serves both from the same account and the same app password.
Everything works on several nodes over one PostgreSQL and in an application published with NativeAOT. This is the
address-book twin of the published calendars of spec 0006, and reuses its store, journal, host and credentials
unchanged.

## User scenarios

### Publish a contact a person sees on their phone (P1)

- Given a module that publishes a customer as a contact in its create-customer handler, when the handler's
  transaction commits, then the contact is in the published address book; when the transaction rolls back, then it is
  not.
- Given a published contact, when the handler publishes the same contact again unchanged, then the address book's sync
  token does not change.
- Given a published contact, when the handler publishes it with a changed phone number, then the sync token changes,
  the contact's ETag changes, and a client syncing from the previous token receives that contact as changed.
- Given a published contact, when the handler removes it, then a client syncing from the previous token receives it as
  removed.
- Given an address book with 300 published contacts, when the application replaces its content with 299 of them, one
  of them changed, then a client syncing from the previous token receives exactly one change and one removal.
- Given a contact with a photo, a birthday without a year and an organization, when it is published, then a client
  receives one vCard 3.0 card with the photo inline, the birthday and the organization, under the contact's UID.

### Publish a group inside an address book (P2)

- Given a group "Team red" whose members are two published contacts, when it is published, then a client fetching it
  as vCard 3.0 receives `X-ADDRESSBOOKSERVER-KIND:group` with both members, and one asking for 4.0 receives
  `KIND:group` with both members, under the same ETag.
- Given a published group, when a client sends an `addressbook-query` for `KIND` equal to `group`, or for
  `X-ADDRESSBOOKSERVER-KIND` equal to `group`, then the group is answered and the members are not.
- Given a group with 2,500 members and `CardDav:ContactRead:MaxPropertiesPerContact` raised to 3,000, when it is
  published and fetched as 4.0, then the card holds all 2,500 members; with the default limit, publishing it throws.

### Decide who sees which address book in a handler (P1)

- Given an address-book source `customers` whose handler requires the permission `customers.read` and returns one
  address book "Customers", when a user without that permission connects, then their address-book home lists no
  customer address book and its URL answers `404`.
- Given the same source, when a user with the permission connects, then their home lists exactly the address books
  the handler returned, with the display name and description it returned.
- Given a team address book the source returns to the members of a team through a resource rule, when a member leaves
  the team, then on the next sync the address book is gone from that person's home.
- Given the source's module disabled by configuration, or the source's handler behind a closed feature gate, when a
  user connects, then no address book of that source is listed or served.
- Given an address-book home URL with another user's id, when a user requests it, then the answer is `404`.
- Given an address-book source registered without a handler, when the host starts, then the start fails with a message
  that names the source.

### Add an account in a contacts app (P1)

- Given a host serving DAV under `/dav`, when a person adds a CardDAV account on iOS with only the host name, their
  e-mail and an app password, then the account is created and the contacts of the person's address books appear.
- Given the same host, when the account is added in macOS Contacts, Thunderbird, DAVx5 and Evolution, then each shows
  the same address books.
- Given a host that serves calendars and address books, when a person adds the account in DAVx5 or macOS with one app
  password, then the calendars and the address books are both found.
- Given a published address book, when a client asks for its privileges, then it receives `read` and
  `read-current-user-privilege-set` only, and Thunderbird, DAVx5 and Evolution show the address book as read-only.
- Given a read-only address book, when a client sends `PUT`, `DELETE`, `PROPPATCH`, `MKCOL`, `MOVE` or `COPY`, then the
  answer is `403` with the `need-privileges` precondition, and nothing changes.
- Given a client holding a sync token older than the journal's retention, when it syncs, then the answer is `403` with
  `valid-sync-token` in the body, and the client resyncs the address book in full and ends with the same contacts.
- Given a client that asks for vCard 4.0 in its `address-data`, when it fetches contacts, then it receives each
  contact as vCard 4.0 with the same ETag, and a client that names no version receives vCard 3.0.
- Given a principal, when DAVx5 sends `OPTIONS` to it before accepting it, then the `DAV` header holds `addressbook`.

### Search an address book from a client (P2)

- Given an address book with contacts "Anna Berger" and "Bernd Huber", when a client sends an `addressbook-query` for
  `FN` containing `ber`, then both are answered; for `FN` starting with `ber`, then only "Bernd Huber".
- Given a contact with a work and a home e-mail address, when a client queries for an `EMAIL` whose `TYPE` parameter
  equals `work` and whose value ends with `@example.com`, then the contact is answered only if its work address matches.
- Given an `addressbook-query` with a `limit` of 10 that matches 25 contacts, when it is answered, then the answer holds
  10 contacts and says, with `507` on the request URL, that more matched.
- Given an `addressbook-query` without a `filter` element, when it is answered, then every contact of the address book
  is in the answer.
- Given an `addressbook-query` with a collation the server does not support, when it is answered, then the answer is
  `403` with the `supported-collation` precondition.

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

Package and shape

- FR-001: The feature MUST ship as the package `Elarion.CardDav`, depending on `Elarion.WebDav`, `Elarion.Contacts`
  and `Elarion.Abstractions` only; it MUST NOT depend on ASP.NET Core, EF Core or `Elarion.CalDav`, and MUST NOT expose
  a type of a third-party library in its public API.
- FR-002: `Elarion.CardDav` MUST be AOT-compatible, and serving address books MUST NOT use reflection, so that an
  application published with NativeAOT gets no trimming or AOT warning from it.
- FR-003: Registration MUST be `AddElarionCardDav`, taking an optional `IConfiguration` and an optional options
  callback; `CardDavOptions` MUST embed a `ContactReadOptions` for contact data instead of limits of its own.
- FR-004: The store, its journal and retention, the host, the request limits and the app-password scheme of spec 0006
  MUST serve address books without a new package, table or route group: spec 0006 FR-020, FR-021, FR-025, FR-033,
  FR-034 and FR-036 apply to address books as written there for calendars.

Publishing

- FR-005: `ICardDavPublisher` MUST publish a contact (an `Elarion.Contacts` `Contact`), remove a contact by UID, and
  replace the content of an address book with a set of contacts, addressed by address-book source and address-book key
  (`CardDavAddress`).
- FR-006: Publishing MUST take part in the caller's unit of work, so the change is visible to clients exactly when the
  caller's transaction commits.
- FR-007: Publishing a contact whose written bytes equal the stored ones MUST NOT change the address book's sync token.
- FR-008: Every other publish, remove and replace MUST change the address book's sync token, and replacing MUST record
  each contact it adds, changes and removes.
- FR-009: A contact MUST be stored as vCard 3.0 written by the `Elarion.Contacts` writer, and its ETag MUST be strong
  and derived from the stored bytes.
- FR-010: Publishing a contact without a UID, with an empty display name, or one the `Elarion.Contacts` writer
  refuses, MUST throw, because it is the application's mistake, not a user's input; replacing with two contacts of one
  UID MUST throw as well.

Address-book sources and access

- FR-011: A module MUST declare an address-book source as a query request implementing `ICardDavSource<TSelf>` (which
  states the source's name) whose handler returns the address books of that source the current user may see, each with
  a key, a display name and optionally a description, and MUST register it with `AddElarionCardDavSource<TQuery>()` in
  the module's `ConfigureServices`.
- FR-012: Every DAV request MUST invoke the address-book sources through the handler pipeline as the authenticated
  user, so `[Require*]`, resource rules and feature gates apply, and MUST serve only address books a source returned.
- FR-013: An address book no source returns, a source whose module is disabled or whose feature gate is closed, and an
  address-book home URL of another user MUST answer `404`.
- FR-014: Two address-book sources with the same name, and a source name or address-book key that is not 1 to 64
  characters of letters, digits, `-` and `_`, MUST fail at startup or when returned, naming the source; publishing into
  a source that is not registered MUST throw; a registered source without a handler MUST fail the host's start, naming
  the source.
- FR-015: An address-book source and a calendar source MAY share a name; they MUST NOT share content or sync state.

The CardDAV server

- FR-016: The host MUST serve, under the WebDAV prefix (default `/dav`), the address-book home
  `/dav/addressbooks/{user}/`, each address book at `/dav/addressbooks/{user}/{segment}/` and each contact below it
  as `{name}.vcf`, and the principal `/dav/principals/{user}/` MUST be the same resource CalDAV uses.
- FR-017: `/.well-known/carddav` at the application root MUST redirect `PROPFIND`, `GET`, `HEAD` and `OPTIONS` with
  `301` to the prefix.
- FR-018: `OPTIONS` on every DAV URL MUST answer `200` with a `DAV` header that holds `addressbook` (and
  `calendar-access` when CalDAV is registered too) and an `Allow` header that lists no write method.
- FR-019: `PROPFIND` with depth 0 and 1 MUST answer the properties RFC 4918, RFC 5397, RFC 6352 and RFC 6578 define for
  the principal, the address-book home and address books (among them `current-user-principal`, `principal-URL`,
  `addressbook-home-set`, `displayname`, `resourcetype` with `addressbook`, `addressbook-description`,
  `supported-address-data`, `supported-report-set`, `current-user-privilege-set`, `owner`, `sync-token`, `getetag`,
  `getcontenttype`), and `getctag`, and MUST answer an unknown property with `404` inside the `multistatus`.
- FR-020: `supported-address-data` MUST list `text/vcard` in the versions 3.0 and 4.0.
- FR-021: `REPORT` MUST support `sync-collection` (RFC 6578) on a published address book, and `addressbook-multiget`
  and `addressbook-query` (RFC 6352) on every address book.
- FR-022: `addressbook-query` MUST support `prop-filter`, `param-filter`, `text-match` with the match types `equals`,
  `contains` (the default), `starts-with` and `ends-with` and with `negate-condition`, `is-not-defined`, and the `test`
  attribute (`anyof`, the default, and `allof`) on `filter` and `prop-filter`; a query without a `filter` MUST match
  every contact, as Evolution sends it.
- FR-023: `text-match` MUST support the collations `i;unicode-casemap` (the default), `i;ascii-casemap` and `i;octet`,
  and an unknown collation MUST answer `403` with the `supported-collation` precondition.
- FR-024: An `addressbook-query` with a `limit` lower than the number of matches MUST answer that many contacts and a
  `507` response for the request URL inside the `multistatus`.
- FR-025: `address-data` without a version, or with `version="3.0"`, MUST answer the stored vCard 3.0; with
  `version="4.0"` it MUST answer the same contact written as vCard 4.0; another content type or version in an
  `addressbook-multiget` or `addressbook-query` MUST answer `403` with the `supported-address-data` precondition.
- FR-026: `address-data` naming properties (`prop name="…"`) MUST answer only those properties, together with
  `VERSION`, `UID` and `FN`, which a card always needs (Apple drops a card without `FN`).
- FR-027: The ETag of a contact MUST be the same whichever version `address-data` asks for.
- FR-028: `GET` and `HEAD` on a contact MUST answer its stored bytes with `Content-Type: text/vcard; charset=utf-8` and
  its `ETag`, MUST honour `If-None-Match`, and MUST answer vCard 4.0 when the `Accept` header prefers
  `text/vcard; version=4.0`.
- FR-029: Every address book MUST grant `read` and `read-current-user-privilege-set` only, and every write method MUST
  answer `403` with the `need-privileges` precondition and change nothing.
- FR-030: An app password with the `dav` scope MUST authenticate address-book requests exactly as calendar requests,
  and a feed token MUST NOT authenticate any address-book URL.
- FR-031: Every published card MUST carry a non-empty `FN`, and `N`, `UID` and `VERSION`, because Apple drops a card
  without `FN`.
- FR-032: A group contact (spec 0005 FR-068–FR-071) MUST publish, sync and convert between vCard 3.0 and 4.0 like any
  contact; its members MUST NOT be checked against the address book.
- FR-033: An `addressbook-query` `prop-filter` named `KIND` or `MEMBER` MUST also match the stored
  `X-ADDRESSBOOKSERVER-KIND` and `X-ADDRESSBOOKSERVER-MEMBER` properties.
- FR-034: Publishing a contact with more properties than `CardDavOptions.ContactRead.MaxPropertiesPerContact` MUST throw,
  so that every stored card can be read back to convert or query it.

Live address books and URL segments (ADR-0113, amendment 2026-11-27; FR-043–FR-046 its second amendment)

- FR-035: An address-book source registered with `AddElarionCardDavLiveSource<TQuery, TAddressBooks>()` MUST serve live
  address books: the source's handler MUST decide which address books the user sees, as for a published source, and
  `TAddressBooks` (an `ICardDavLiveAddressBooks` resolved from the request's scope) MUST list, and give, the contacts
  of each for the current user on every request, without the store.
- FR-036: A live address book MUST answer `getctag` with the CTag `ICardDavLiveAddressBooks.GetCTagAsync` returns
  (by default a hash of the listing's names and ETags), MUST NOT define `sync-token`, MUST NOT list `sync-collection`
  in `supported-report-set`, and MUST answer a `sync-collection` with `403` and the `supported-report` precondition.
- FR-037: A `PROPFIND` listing of a live address book MUST write no card unless it names `address-data` (FR-044), and its members' `getcontentlength` MUST
  answer `404` inside the `multistatus`.
- FR-038: A live address book MUST ask its contacts for the purpose of the request (`DavContentPurpose`: `Get`,
  `Multiget` or `Query`), so an application can leave heavy parts such as photos out of a query.
- FR-039: A live contact MUST be written as vCard 3.0 with the rules of FR-009–FR-010 and FR-031, and served with the ETag
  and member name the application gave; an invalid name or ETag MUST throw as the application's mistake.
- FR-040: A contact a viewer does not see MUST answer `404` to a `GET` and inside a multiget, and a change of what the
  viewer sees MUST change the CTag without any publish.
- FR-041: Publishing into a live source MUST throw.
- FR-042: An address book's URL segment MUST be `{source}.{key}` unless the source names one
  (`PublishedAddressBookInfo.Segment`: 1 to 128 ASCII letters, digits, `-`, `_`, `.`, `~` or `@`, not `.` or
  `..`), MUST be matched exactly, MUST NOT change the store address or the sync token, and two address books a user
  sees with the same segment, or an invalid segment, MUST throw.
- FR-043: An application whose address-book sources are all live MUST build and start without an
  `IDavCollectionStore` (`ValidateOnBuild` included); `ICardDavPublisher` MUST still resolve, and publishing into a
  published source without a store MUST throw naming the store to register.
- FR-044: A `PROPFIND` that names `address-data` MUST carry each member's card: at depth 1 every member of the address
  book, read with `DavContentPurpose.Multiget` from a live source, and at depth 0 the member; a member whose card was not
  loaded MUST answer `address-data` in a `404` propstat, never empty in a `200` one.
- FR-045: A live source MAY give a contact as its property list (`CardDavLiveContact(name, etag, properties)`), which
  MUST be written as vCard 3.0 in its order and spelling, as `ContactWriter.AddAsync(IReadOnlyList<ContactProperty>)`
  writes it; a list without a `UID` or an `FN` MUST throw as the application's mistake.
- FR-046: A `PROPFIND` without a `Depth` header MUST be answered as depth 1 (ADR-0111 plan, sabre/dav's reading), not
  as RFC 4918's `infinity`, which FR-018 of spec 0006 refuses.

## Success criteria

- SC-001: An account added with only host name, e-mail and app password shows the published address books on iOS,
  macOS, Thunderbird, DAVx5 and Evolution (on iOS and macOS: an account whose home holds one address book), read-only
  where the client honours the privileges, verified once by hand per client before release.
- SC-002: A change published in a handler reaches a polling client in one sync, and a client that syncs 10,000 times
  against an unchanged address book causes no write to the database.
- SC-003: Answering a sync of an unchanged address book of 1,000 contacts takes under 20 ms on the benchmark machine.
- SC-004: A host published with NativeAOT serves address books without trimming or AOT warnings.

## Out of scope

- Two-way editing from contacts apps (`PUT`/`DELETE` into an application's data); ADR-0111 describes its seam, and it
  would come for calendars and address books together.
- A CardDAV client that syncs external address books into an application.
- vCard feed URLs: no contacts app subscribes to an address-book URL without an account (ADR-0113).
- The directory gateway of the expired draft `draft-daboo-carddav-directory-gateway` (Apple's search-only directory)
  and server-side search across users.
- Merging several address books into one, or scoping an account to one address book, for Apple clients, which use one
  address book per account (ADR-0113).
- Converting other formats than vCard 3.0 and 4.0 (jCard, `text/x-vcard`, vCard 2.1).
- Push to clients; clients poll.
- A store for the EF-free `Elarion.Sql` tier.

## Decisions

- [ADR-0113](../docs/decisions/0113-carddav-address-books.md): what CardDAV adds to ADR-0111.
- [ADR-0114](../docs/decisions/0114-contact-groups.md): group cards, which amend ADR-0113 section 4.
- [ADR-0111](../docs/decisions/0111-caldav.md): the WebDAV core, store, host and app passwords this builds on.
- [ADR-0110](../docs/decisions/0110-vcard-contacts.md): the contact model, reader and writer.
