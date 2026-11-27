---
type: Architecture Decision Record
title: "ADR-0114: Contact groups — a group kind and members in the contact model, written in Apple's form in vCard 3.0 and as KIND:group in 4.0"
description: "Elarion.Contacts reads and writes group cards: a contact of kind group holds member references, read from KIND:group/MEMBER and Apple's X-ADDRESSBOOKSERVER properties and written in the form each version's clients read; Elarion.CardDav publishes and converts them like any contact, so a published address book can carry groups."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T19:00:00Z }
status: stable
---
# ADR-0114: Contact groups — a group kind and members in the contact model, written in Apple's form in vCard 3.0 and as KIND:group in 4.0

- Status: Accepted
- Date: 2026-11-27
- Amends: [ADR-0110](0110-vcard-contacts.md) (group cards were out of scope of the contact model) and
  [ADR-0113](0113-carddav-address-books.md) section 4 (contact groups were left out of CardDAV).
- Related: the research notes [DAV conformance suites](../research/2026-11-27-dav-conformance-suites.md) and
  [CardDAV clients](../research/2026-11-27-carddav-clients.md), specs [0005](../../specs/0005-vcard-contacts.md) and
  [0007](../../specs/0007-carddav-address-books.md).

## Context

ADR-0110 left group cards out of the contact model and ADR-0113 left them out of CardDAV, because the clients disagree
on the form and no product needed them. Porting other projects' tests showed that the disagreement has a known answer
that sabre/vobject and Cyrus both implement, and that the cases are few and concrete:

- **Two forms, one meaning.** vCard 4.0 (RFC 6350 §6.1.4, §6.6.5) has `KIND:group` and one `MEMBER` per member, each a
  URI, usually `urn:uuid:<member UID>`. vCard 3.0 has no group; Apple writes `X-ADDRESSBOOKSERVER-KIND:group` and
  `X-ADDRESSBOOKSERVER-MEMBER`, and every client that reads 3.0 groups reads that form. sabre/vobject converts one into
  the other in both directions (its `testConvertGroupCard`), and drops `KIND:individual` when it writes 3.0.
- **Clients.** Apple's Contacts shows group cards as groups. DAVx5 shows them as groups when the account uses "groups
  are separate vCards" (it writes and reads the Apple form on 3.0 servers, `KIND`/`MEMBER` on 4.0 ones); in its
  "categories" mode it groups by `CATEGORIES` instead. Thunderbird has no CardDAV groups and shows a group card as an
  ordinary card. Evolution's handling is unverified.
- **What breaks.** Cyrus tests a group with 2,500 members and searches `X-ADDRESSBOOKSERVER-KIND` and
  `X-ADDRESSBOOKSERVER-MEMBER` with `addressbook-query`; a group card without `N` gets one in 3.0.

An application that publishes teams, departments or mailing lists as address books can now also publish them as groups
inside one address book, which is the only way Apple clients, limited to one address book per account (ADR-0113
section 5), show subsets.

## Decision

1. **The model.** `ContactKind` gains `Group`. `Contact` gains `Members`, the member references as URIs in order;
   `ContactMember.FromUid(uid)` builds the `urn:uuid:` reference to another contact. A contact that is not a group has
   no members: writing one with members fails like any contact the writer cannot represent.
2. **Reading.** `KIND:group` (any case) and `X-ADDRESSBOOKSERVER-KIND:group` make a group; every `MEMBER` and
   `X-ADDRESSBOOKSERVER-MEMBER` value is a member, in file order, whichever version the card has. `KIND:individual` is
   an individual. The properties stay in the lossless property list as read.
3. **Writing.** 3.0 writes a group as `X-ADDRESSBOOKSERVER-KIND:group` with one `X-ADDRESSBOOKSERVER-MEMBER` per
   member, and `N` with the group's name as its family name (Apple's and Cyrus's form); 4.0 writes `KIND:group` with one
   `MEMBER` per member. So a group read in one version and written in the other converts, as sabre/vobject's round trip
   requires, and a 4.0 card is never left with Apple's 3.0 properties.
4. **CardDAV.** A group is a contact: `ICardDavPublisher` publishes it, the store keeps the 3.0 card, `address-data`
   version 4.0 converts it through the model. A `prop-filter` named `KIND` or `MEMBER` also matches the stored Apple
   form (`X-ADDRESSBOOKSERVER-KIND`, `X-ADDRESSBOOKSERVER-MEMBER`), so a 4.0 client's search finds what it sees.
   Members are not checked against the address book: a member a client cannot resolve is shown or ignored by the
   client, as with any server.
5. **Limits.** Members count against `ContactReadOptions.MaxPropertiesPerContact` (1,000 by default) when a card is read
   back; publishing a contact with more properties than the configured limit throws, so every stored card converts. An
   application with larger groups raises the limit in `CardDav:ContactRead`.

## Consequences

- An address book can carry groups that Apple and DAVx5 show as groups; Thunderbird users see them as cards. The
  capability page says so, and that DAVx5 users in "categories" mode need `CATEGORIES` on the members, which the
  application sets.
- The contact model grows by one kind and one list; existing contacts read and write as before, except that a card with
  `X-ADDRESSBOOKSERVER-KIND:group` now reads as a group, and a 3.0 group written as 4.0 now converts.
- ADR-0110's and ADR-0113's statements that groups are out of scope no longer hold for group cards; `RELATED` and the
  other unmapped properties stay out.

## Rejected alternatives

- **Groups by `CATEGORIES` only**: no Apple client shows categories as groups, and DAVx5 needs a per-account setting.
- **Writing both forms in one card**: Apple then sees `KIND`, which 3.0 does not define, and a 4.0 client sees duplicates.
- **Checking members against the address book**: the publisher would need the whole address book to publish one group,
  and a member removed later would make a stored group invalid without anything changing it.
- **A separate group type**: a group card is a card with a name, a UID and the other properties; a second type would
  duplicate the reader, the writer and the publisher for two properties.
