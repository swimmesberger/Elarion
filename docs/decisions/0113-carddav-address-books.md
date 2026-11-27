---
type: Architecture Decision Record
title: "ADR-0113: CardDAV — published address books on the WebDAV core, stored as vCard 3.0 and converted on request"
description: "Elarion serves an application's contacts as read-only CardDAV address books from the store, host and app passwords of ADR-0111, declares who sees which address book in address-book sources, stores every contact as vCard 3.0 and converts it to 4.0 when a client asks, searches by parsing cards instead of indexing them, and leaves groups, vCard feeds and a CardDAV client out."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T10:45:00Z }
status: stable
---
# ADR-0113: CardDAV — published address books on the WebDAV core, stored as vCard 3.0 and converted on request

- Status: Accepted
- Date: 2026-11-27
- Related: [ADR-0111](0111-caldav.md) (the WebDAV core, the published-collection store and journal, the host, the
  source pattern and app passwords this builds on), [ADR-0110](0110-vcard-contacts.md) (the contact model, reader and
  writer), the research notes [CardDAV clients](../research/2026-11-27-carddav-clients.md) and
  [vCard libraries and producers](../research/2026-11-26-vcard-libraries-and-producers.md), and
  [spec 0007](../../specs/0007-carddav-address-books.md).

## Context

ADR-0111 designed the WebDAV core so that CardDAV could follow "as an `Elarion.CardDav` package with address-book
sources and a publisher over the same store, journal, host and app passwords". That part needs no new decision: an
address book is a published collection of kind `addressbook`, an address-book source is a query handler like a
calendar source, and the same app password authenticates both. The consuming application whose hand-built read-only
CardDAV server informed ADR-0111 is the first user.

What ADR-0111 does not decide is what is particular to contacts:

- **Which vCard version is stored and served.** RFC 6352 makes vCard 3.0 mandatory and lets a client ask for another
  version in `address-data`. iOS and macOS ask for 3.0 explicitly and sync only part of an address book when a server
  answers 4.0 anyway; DAVx5 asks for 4.0 when the server lists it; Thunderbird and Evolution send a bare
  `address-data` and read what comes (research notes).
- **How `addressbook-query` is answered.** Calendar queries select candidates by an indexed time span; contacts have no
  such dimension, and a query matches any property or parameter text.
- **What address-book features to leave out**: contact groups, vCard feeds by URL, a CardDAV client, the directory
  gateway.
- **Apple uses one address book per account.** macOS downloads one address book of an account, chosen unpredictably,
  and iOS at best merges several into one list; Thunderbird, DAVx5 and Evolution list each and let the person choose.

## Decision

### 1. An `Elarion.CardDav` package on the WebDAV core, with the source machinery shared

`Elarion.CardDav` depends on `Elarion.WebDav`, `Elarion.Contacts` and `Elarion.Abstractions`, is AOT-compatible and
registers a second `IDavProtocol` (home segment `addressbooks`, well-known name `carddav`, compliance class
`addressbook`). It does not depend on `Elarion.CalDav`: an application may serve contacts without calendars. The host,
the store and the app-password packages do not change; one principal carries both home sets, so one account in DAVx5
or macOS finds calendars and address books.

The source registration, the start check that every source has a handler, the invocation that maps a refusal to "no
collections", the stable member names and the `text-match` collations move from `Elarion.CalDav` into
`Elarion.WebDav` as internals both protocols share. The public types stay per protocol (`ICardDavSource<TSelf>`,
`CardDavSourceListing`, `PublishedAddressBookInfo`, `ICardDavPublisher`, `CardDavAddress`), following ADR-0111's
naming rule: `Contact…` is `Elarion.Contacts`', `CardDav…` is DAV-only.

Rejected: **one generic `IDavSource<TSelf, TInfo>`** for both protocols. It would save two interfaces but make the
handler signature of an application's calendar source mention a WebDAV type parameter, and the two infos differ (time
zone, color and component kinds against a description).

### 2. Contacts are stored as vCard 3.0 and converted to 4.0 when asked

The publisher writes each `Contact` with the `Elarion.Contacts` writer as vCard 3.0 and stores those bytes; the ETag is
their hash. `supported-address-data` lists 3.0 and 4.0. A client that asks for 4.0 in `address-data` gets the stored
card read back into a `Contact` and written as 4.0, with the same ETag; a request for named properties gets the card
pruned to those properties plus `VERSION`, `UID` and `FN`. `GET` answers the stored 3.0 bytes.

3.0 is stored because it is what every client reads (iOS and macOS only write 3.0, Android's importer rejects 4.0,
ADR-0110 made it the writer's default), so the common request is served from the stored bytes without parsing. The
ETag identifies the contact's content, not the representation, as sabre/dav does when it converts: a client that
asks for 4.0 always asks for 4.0, so it never compares a 3.0 tag against 4.0 bytes, and the tag changes exactly when the
contact does.

Rejected: **storing both versions**, which doubles the store for a conversion that costs one parse of one card;
**storing 4.0** and converting down for the clients that matter most; and **not offering 4.0**, which spec 0005's
writer supports and DAVx5 prefers.

### 3. `addressbook-query` parses the cards; nothing is indexed

An `addressbook-query` reads the address book's cards from the store, parses each with the `Elarion.Contacts` reader,
and matches the filter against the card's property list (so groups, `X-` properties and parameters are searchable as
RFC 6352 §10.5 describes). The collations are `i;unicode-casemap` (CardDAV's default), `i;ascii-casemap` and
`i;octet`, shared with CalDAV.

No client syncs by query (research note): Thunderbird, DAVx5 and Evolution search their local copy, Evolution's one
query carries no filter, and only Apple's directory search, which section 4 leaves out, sends filters. Queries come
from a person or a tool searching, rarely, over address books of a few thousand contacts at the tier ADR-0025 sets. An index of searchable values would need a table, a write per published
property and a schema for what is searchable, for a path that is cold. If a client turns out to query on every sync,
an index is added behind the store seam without changing the protocol.

### 4. What is left out

- **Contact groups** (`KIND:group` with `MEMBER` in 4.0, Apple's `X-ADDRESSBOOKSERVER-KIND:group` in 3.0). Clients
  disagree on the form (DAVx5 lets the user choose between group cards and `CATEGORIES`, Apple uses only its own,
  Thunderbird has no CardDAV groups and shows a group card as a contact), a group card must be kept consistent with its
  members on every change, and the `Contact` model of ADR-0110 has no group kind. An application groups by address book, which every client shows, or by `CATEGORIES`, which the published card
  carries.
- **vCard feeds.** Unlike calendars, no contacts app subscribes to a URL without an account: Outlook and Google Contacts
  import a `.vcf` file once and cannot add a CardDAV account at all (research note). A one-off export is the
  application's download of `ContactWriter.ToStreamedFile`, not a DAV feature, so feed tokens stay calendar-only.
- **A CardDAV client.** No product needs to sync external address books yet; the CalDAV client's step design applies
  unchanged when one does.
- **The directory gateway** of the expired draft `draft-daboo-carddav-directory-gateway` and server-side search across
  users: only Apple uses it, as a search-only directory that cannot be browsed, which would hide the published contacts.
- **Two-way editing**, as for calendars (ADR-0111, section 3).

### 5. One address book per account on Apple is documented, not worked around

A source may return several address books, and the home lists every one the sources return. Thunderbird, DAVx5 and
Evolution show each. For iOS and macOS the capability page tells the application to publish one address book per user
when it serves Apple users: an address-book source that returns one book ("Contacts") holding every contact the user
may see, beside or instead of finer ones. The application knows which of its contacts belong together; the framework
does not.

Rejected, as revisable:

- **App passwords scoped to one address book**, so that each address book is an account of its own on Apple devices.
  It works within Apple's rule but makes a person create and type one password per address book, and widens the
  app-password scope and the dispatcher's filtering for one vendor's limitation. It is the first thing to add if
  applications ask for several address books on Apple devices.
- **A merged address book the framework builds** from all of a user's address books: a contact in two books would have
  two URLs in one collection, or the merge would need its own journal; either breaks exact sync or duplicates the store.
- **Group cards for subsets of one address book**, Apple's own model: left out with groups (section 4).

## Consequences

- An application gets CardDAV for what it already shows by publishing contacts in its handlers and writing one query
  handler per address-book source; the same app password and host serve its calendars.
- A refactor moves CalDAV's source machinery into the WebDAV core before CardDAV builds on it; public CalDAV types do
  not change.
- A request for vCard 4.0 or for named properties parses one card per member; the common 3.0 request does not.
- A search parses every card of the address book. At the tier this is milliseconds; beyond it, the store gains an
  index.
- The glossary gains *published address book* and *address-book source*.

## Rejected alternatives

- **A generic source interface for both protocols**: section 1.
- **Storing both versions, storing 4.0, or 3.0 only**: section 2.
- **An index of searchable values**: section 3.
- **Group cards, vCard feeds, a client, the directory gateway**: section 4.
- **Scoped app passwords, a merged address book, group cards for Apple**: section 5.

## Amendment (2026-11-27): live address books answered by a CTag, and explicit URL segments

An application adopting the packages needed what section 5 and ADR-0111 section 3 do not serve: an address book whose
contacts are filtered per viewer by row-level rules (restricted contacts, sharing groups, administrators) that change
without any contact changing (group membership, a permission, a flag on the contact), with per-viewer content (a
fallback photo only for viewers who may see companies), and at URLs every synced device already stored
(`/addressbooks/{user}/default/`).

Publishing one address book per viewer (ADR-0111 section 3) can follow those rules only with a projection the
application would have to build: re-publish every viewer's book on every contact edit and on every visibility change,
with photo bytes copied per viewer. Getting it wrong leaks contacts, and the copy is a viewer count times the contacts.
At the tier of ADR-0025 the cheaper and safer answer is to read the rows per request, the way the application's own
list queries do, and to give up exact sync for those books.

**Decision.** `Elarion.WebDav` gains a second kind of collection: a **live collection**, whose members a protocol reads
per request through `IDavLiveCollection` (`ListMembersAsync` → name and ETag, `GetMembersAsync(names, purpose)` → the
content, `GetCTagAsync`), instead of from the store. `DavCollection.Live` marks it. A live collection is answered by
its CTag only: `getctag` carries it, `sync-token` is not defined, `sync-collection` is neither in
`supported-report-set` nor answered (`403` with `supported-report`), and the listing may leave `getcontentlength` out
(`DavMember.Size` is now optional). Every surveyed client handles a CTag-only collection: it compares the CTag, then the
ETags of a `Depth: 1` listing, and fetches what changed with a multiget. `DavContentPurpose` (`Metadata`, `Get`,
`Multiget`, `Query`) tells the source why it is asked, so it can leave photos out of a query, which reads every contact.

`Elarion.CardDav` exposes it as a **live address-book source**: `AddElarionCardDavLiveSource<TQuery, TAddressBooks>()`.
The source's handler still decides, through the handler pipeline, which address books the user sees; the scoped
`ICardDavLiveAddressBooks` decides which contacts each holds for that user, with the same resource rules
(`WhereAuthorized`) the application's list handlers use, and returns `Contact`s that the framework writes as vCard 3.0
with the application's member names and ETags. Its default CTag is a hash of the listing, which changes exactly when the
listing does. Publishing into a live source throws.

Both protocols' sources may also name a collection's **URL segment** (`PublishedAddressBookInfo.Segment`,
`PublishedCalendarInfo.Segment`), default `{source}.{key}`: it is a URL contract, matched exactly, validated (one path
segment of 1 to 128 safe characters, unique within the home), and it does not change the store address or the sync
token.

**What changes in earlier decisions.** ADR-0111 section 2 rejected *handler-backed serving* because every application
would rebuild the change journal and token expiry. That rejection stands for sync tokens: a live collection has none,
so nothing of the journal is rebuilt. ADR-0111's rejected alternative *CTag only* stands as the default: published
collections keep exact sync, and live ones are the opt-in for content the store cannot follow. ADR-0111's other
rejection, *a backend interface outside the handler pipeline*, is narrowed rather than reversed: which collections a user
sees is still a handler; only the rows inside a live collection come from a scoped service, which reads them with the
application's own authorization.

**Trade-offs.**

- A change to a live address book costs every client a full listing (names and ETags, no cards) on its next poll after
  the CTag changed, and computing the default CTag costs a listing per poll. At a few thousand contacts per book and the
  polling intervals of contacts apps this is one indexed query per poll.
- Removals and visibility losses are exact without a journal: what the viewer no longer sees is missing from the
  listing, and the client deletes it.
- The application owns the ETag's correctness: one that does not change with the card keeps a stale copy on devices;
  one that changes without the card makes clients refetch. The capability page says what to derive it from.
- Two collection kinds in the dispatcher. They share every path but the member reads and the sync report, through
  `DavContext.ListMembersAsync` and `GetMembersAsync`.

Rejected, as revisable:

- **A token for live collections** (answer `sync-collection` with no changes when the token equals the CTag, else
  `valid-sync-token`): every sync then fails and falls back, and a client that prefers tokens re-lists in full anyway;
  advertising nothing is simpler and every client handles it.
- **Live sources through the handler pipeline** (one query request per call with names and purpose): the requests would
  need the framework to construct application types with arguments, a generator or reflection, for calls that are
  internal to one DAV request; the decision of who sees which book stays a handler.
- **A projection helper that re-publishes per viewer**: the projection is the application's domain knowledge (which
  change affects whom), and the copy is per viewer.
- **Case-insensitive segment matching**: a collection would have several URLs, but multiget hrefs and sync answers
  name one, so a client that used another spelling would see every contact as missing. Clients use the hrefs the home
  listed.

## Amendment (2026-11-27, second): live-only applications, cards in a PROPFIND, the application's own card bytes

The application that adopted live address books found that it still had to carry the DAV store, that a listing naming
`address-data` answered it empty, and that its own card bytes could not reach devices. Spec 0007, FR-043–FR-046, holds
the requirements.

1. **An application whose address books are all live registers no store.** `AddElarionCardDav` registers
   `ICardDavPublisher` unconditionally, and its default took `IDavCollectionStore` in its constructor, so dependency
   validation failed without `AddElarionWebDavEntityFrameworkCore` and its three tables, which such an application
   migrated empty. The publisher now resolves the store on first use from the request's scope, as `DavContext.Store`
   already does, and says which store to register when there is none. *Rejected:* registering the publisher only when
   a published source or a store is registered, which depends on registration order (a store is often added after
   CardDAV) and would make a handler that injects the publisher fail validation in one configuration and not another.
2. **A `PROPFIND` that names a member's data property carries the data.** `DavResource.Content` read a missing content
   as an empty one (a `null` in a conditional converted to `ReadOnlyMemory<byte>` through its implicit conversion from
   an array), so `address-data` and `calendar-data` were "defined" for every listed member and answered empty in a `200`
   propstat. Fixed, and beyond answering `404`: when the selection names one of the protocol's member properties, the
   dispatcher loads the members with their content, as `sync-collection` already does — at depth 1 every member, from a
   live source with `DavContentPurpose.Multiget`, at depth 0 the one member with `Get`. RFC 6352 §10.4 and RFC 4791 §9.6
   allow the properties in a `PROPFIND`, and sabre/dav answers them. *Multiget, not Query*, for the live source: a
   client keeps a card it was given under the member's ETag, and a query may leave the photo out, so a card from a
   listing must be the one a `GET` answers. *Rejected:* answering `404` only, which is correct but leaves a client that
   fetches cards this way with none.
3. **A live source may give the card's property list** (`CardDavLiveContact(name, etag, properties)`), written as
   vCard 3.0 by `ContactWriter.AddAsync(IReadOnlyList<ContactProperty>)` in its order and spelling, with the checks a
   contact gets (a `UID`, an `FN`, the per-card property limit). An application that pinned its wire format before
   adopting live books keeps its bytes, so its devices see no change. `CardDavLiveContact` becomes a record with two
   constructors and nullable `Contact` and `Card`. *Rejected:* raw card text, which would skip the writer's checks and
   control-character removal and would have to be parsed to find its `UID`; an application with text reads it into a
   property list with `ContactReader`. Published sources keep taking `Contact`s: their bytes are hashed into the ETag
   by the framework, and no application asked for more.
4. **A `PROPFIND` without `Depth` stays depth 1.** RFC 4918 §9.1 reads a missing header as `infinity`, which this server
   refuses with `403 propfind-finite-depth` (ADR-0111). Reading it as infinity would turn a request every client means
   as a listing into a refusal; sabre/dav, the server most clients are tested against, reads it as 1, and the plan of
   spec 0006 already chose that. The choice is now in spec 0007 (FR-046), on the CardDAV page, and tested on a live
   address book as well as a published collection.
