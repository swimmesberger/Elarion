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
