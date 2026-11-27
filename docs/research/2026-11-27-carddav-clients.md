---
type: Research note
title: "CardDAV clients: what iOS, macOS, Thunderbird, DAVx5 and Evolution require of a read-only address-book server"
description: "How iOS and macOS Contacts, Thunderbird, DAVx5 and Evolution discover, sync and present CardDAV address books, which vCard version each asks for, how each treats a read-only address book, and what that means for a read-only RFC 6352 server; input for ADR-0113."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-27T12:00:00Z }
status: draft
sources:
  - https://bugs.sogo.nu/view.php?id=6179
  - https://bugzilla.gnome.org/show_bug.cgi?id=794874
  - https://datatracker.ietf.org/doc/draft-daboo-carddav-directory-gateway/
  - https://docs.nextcloud.com/server/latest/user_manual/en/groupware/sync_osx.html
  - https://github.com/Kozea/Radicale/issues/1112
  - https://github.com/Kozea/Radicale/issues/1796
  - https://github.com/Kozea/Radicale/issues/740
  - https://github.com/aluxnimm/outlookcaldavsynchronizer
  - https://github.com/bitfireAT/davx5-ose
  - https://github.com/mozilla/releases-comm-central/tree/master/mailnews/addrbook/modules
  - https://github.com/nextcloud/contacts/issues/1870
  - https://github.com/nextcloud/contacts/issues/213
  - https://github.com/sabre-io/Baikal/issues/1191#issuecomment-2933376247
  - https://github.com/sabre-io/dav
  - https://github.com/tine20/tine20/issues/4758
  - https://gitlab.gnome.org/GNOME/evolution-data-server
  - https://groups.google.com/g/sabredav-discuss/c/K9fVdizwZdg
  - https://help.nextcloud.com/t/sync-specific-addressbook-with-ios/212256
  - https://manual.davx5.com/technical_information.html
  - https://sabre.io/dav/clients/ios/
  - https://sabre.io/dav/clients/osx-addressbook/
  - https://support.apple.com/guide/deployment/depd2181f425/web
  - https://support.google.com/contacts/answer/15147365
  - https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405
  - https://support.stalw.art/t/ios-contacts-and-mac-neo/480
  - https://www.infomaniak.com/en/support/faq/2181/understanding-the-synchronization-limits-for-contacts-calendar-macos-ios-apps
  - https://www.rfc-editor.org/rfc/rfc6352
  - https://www.rfc-editor.org/rfc/rfc6764
  - https://www.thunderbird.net/en-US/thunderbird/91.0/releasenotes/
  - https://www.thunderbird.net/en-US/thunderbird/91.2.0/releasenotes
---
# CardDAV clients: what a read-only address-book server must do

Scope: what iOS/macOS Contacts, Thunderbird (native), DAVx5, Evolution do as CardDAV clients, and what that
means for a read-only RFC 6352 server with Basic auth, sync-collection and a read-only privilege set.

Source quality legend: **[code]** = read in the client's current source; **[RFC]**; **[issue]** = bug tracker /
forum with logs; **[doc]** = vendor docs; **unverified** = no primary evidence found. Apple's CardDAV client is
closed source, so every Apple statement is from server-side logs, implementers' notes, or user reports.

Primary sources read (current master/HEAD, Nov 2026):

- Thunderbird: `mailnews/addrbook/modules/CardDAVUtils.sys.mjs`, `CardDAVDirectory.sys.mjs`,
  `AddrBookDirectory.sys.mjs`, `VCardUtils.sys.mjs` —
  https://github.com/mozilla/releases-comm-central/tree/master/mailnews/addrbook/modules
- DAVx5: `core/src/main/kotlin/at/bitfire/davdroid/servicedetection/DavResourceFinder.kt`, `db/Collection.kt`,
  `resource/remote/BaseWebDavCollection.kt`, `resource/remote/CardDavCollection.kt`,
  `sync/ContactsSyncManager.kt`, `sync/ReadOnlyPolicy.kt`, `sync/SyncManager.kt` —
  https://github.com/bitfireAT/davx5-ose ; manual https://manual.davx5.com/technical_information.html
- Evolution (evolution-data-server): `src/addressbook/backends/carddav/e-book-backend-carddav.c`,
  `src/libedataserver/e-webdav-discover.c` — https://gitlab.gnome.org/GNOME/evolution-data-server
- sabre/dav: `lib/CardDAV/Plugin.php`, `lib/CardDAV/Xml/Request/AddressBookQueryReport.php`,
  `Xml/Property/SupportedAddressData.php` — https://github.com/sabre-io/dav ; client notes
  https://sabre.io/dav/clients/ios/ , https://sabre.io/dav/clients/osx-addressbook/
- RFC 6352 https://www.rfc-editor.org/rfc/rfc6352 , RFC 6764 https://www.rfc-editor.org/rfc/rfc6764

---

## 1. Discovery

| Client | User enters | Order of attempts | SRV | OPTIONS / `DAV: addressbook` |
|---|---|---|---|---|
| Thunderbird | URL or host | if path ≠ `/`: the URL itself; if path = `/`: DNS SRV `_carddavs._tcp` (+ TXT `path=`), then `PROPFIND /.well-known/carddav` (Depth 0, `current-user-principal`), then `PROPFIND /`. Then `addressbook-home-set` on the principal, then Depth 1 on the home for `resourcetype`, `displayname`, `current-user-privilege-set` **[code]** | yes, TLS only, optional | never sends OPTIONS, never reads `DAV:` **[code]** |
| DAVx5 | URL or email | PROPFIND on given URL (`resourcetype`, `displayname`, `current-user-principal`, `addressbook-home-set`, `addressbook-description`), then `/.well-known/carddav`; if still no principal and https/mailto: SRV `_carddavs._tcp.<domain>` → TXT paths → `/.well-known/carddav` → `/` **[code]** | yes, TLS only, fallback | **yes, required**: every `current-user-principal` found is accepted only if `OPTIONS <principal>` returns a `DAV:` header containing `addressbook` (`providesService()`); otherwise "Principal … doesn't provide CARDDAV service" **[code]** |
| Evolution | URL | given URL PROPFIND Depth 0 (`resourcetype`, `current-user-principal`, `principal-URL`, `calendar-home-set`, `calendar-user-address-set`, `addressbook-home-set`), then `/.well-known/caldav` and `/.well-known/carddav` if nothing found **[code]** | no SRV lookup in `e-webdav-discover.c` **[code]** | uses OPTIONS `Allow` only as a fallback for writability (see §4) |
| macOS Contacts | host (+ "Manual"/"Advanced" path) | `PROPFIND /.well-known/carddav` → (404) `PROPFIND /` → `OPTIONS /<principal>/` → PROPFINDs on principal (log from macOS 10.13 `AddressBook/1806`) **[issue]** https://github.com/Kozea/Radicale/issues/740 | sabre: "supports SRV records and well-known URLs" **[doc]** | sends OPTIONS on the principal **[issue]**; whether it rejects a missing `addressbook` token: **unverified** |
| iOS (26.6, CalDAV log, same account framework) | host | `PROPFIND /.well-known/caldav` → 307 → PROPFIND target (`current-user-principal`; `principal-URL` 404 tolerated) → `OPTIONS <principal>` → … **[issue]** https://support.stalw.art/t/ios-26-6-caldav-account-remains-inactive-after-successful-discovery-and-options-on-principal-url/1405 | supported per sabre **[doc]** | OPTIONS on principal observed; requirement **unverified** |

Notes:

- `.well-known` is hit with **PROPFIND**, not GET, by Apple, Thunderbird and DAVx5; the redirect must work for
  PROPFIND (RFC 6764 §5: clients MUST follow redirects; servers SHOULD redirect). Apple follows 307 **[issue]**.
- RFC 6352 §6.1: server **MUST** include `addressbook` in the `DAV:` header of OPTIONS "on any resource that
  supports any address book properties, reports, or methods" — and DAVx5 enforces it on the **principal** URL.
  Advertise `DAV: 1, 3, access-control, addressbook` (plus `calendar-access` if the same principal serves CalDAV)
  on principal, home, books and cards.
- RFC 6352 §9.3 / RFC 6764 §6: SRV + `current-user-principal` is the intended host-only setup. **No client
  requires an SRV record** if `/.well-known/carddav` works on the host the user typed: Thunderbird and DAVx5 fall
  back to well-known, Evolution does not use SRV at all.
- Combined vs separate accounts:
  - DAVx5 always detects CalDAV and CardDAV together into one account (`findInitialConfiguration` per service) **[code]**.
  - Evolution's discover scans for calendars and address books in one pass ("collection" account) **[code]**.
  - Thunderbird: address books are a separate dialog from calendars (separate code paths) **[code]**.
  - macOS: "You can not setup Calendar/Contacts together. You need to setup them separately" (CardDAV account
    type in Internet Accounts) **[doc]** https://docs.nextcloud.com/server/latest/user_manual/en/groupware/sync_osx.html
  - iOS: separate "Add CardDAV Account" / "Add CalDAV Account" under Settings → Apps → Contacts/Calendar →
    Accounts → Add Account → Other; configuration profiles have separate CardDAV and CalDAV payloads
    https://support.apple.com/guide/deployment/depd2181f425/web **[doc]**. iOS 26 reportedly leaves an account
    "Inactive" when added via the Mail path; the per-app path works **[issue]** (Stalwart link above).
- CardDAV and CalDAV homes differing: **no client requires it** (none found). Nextcloud/sabre serve both
  `calendar-home-set` and `addressbook-home-set` from one principal. DAVx5/Evolution/Thunderbird filter children by
  `resourcetype`. Keep the homes distinct anyway (cleaner Depth 1 listings); not a requirement.
- Old Apple path quirks: sabre notes iOS 6/7 trouble when the server isn't at the domain root, fixed by a
  well-known endpoint or by entering the full principal URL **[doc]** https://sabre.io/dav/clients/ios/ .
  macOS `@` in usernames not URL-encoded on 10.6 **[doc]**. HTTPS mandatory on iOS ≥ 12 and macOS **[doc]**.

## 2. Properties clients read, address-data versions, number of address books

**Thunderbird [code]**: home Depth 1: `resourcetype` (must contain `CARD:addressbook`), `displayname`,
`current-user-privilege-set`. Book: `getetag`, `CS:getctag` (requested but never used), `sync-token`,
`CARD:supported-address-data` (once per session). Only 200 propstats count. Lists all books; user picks.

**DAVx5 [code]**: collection refresh reads `resourcetype`, `displayname`, `CARD:addressbook-description`,
`current-user-privilege-set`; before each sync `supported-report-set`, `sync-token`, `CS:getctag`,
`CARD:max-resource-size`, `CARD:supported-address-data` (`queryCapabilities()`). **sync-collection is used only
if `supported-report-set` lists `DAV:sync-collection`** (`canCollectionSync`); otherwise PROPFIND+etag. Sync state:
`sync-token` preferred, `getctag` fallback (`ResponseExt.syncState()`). Each book can be selected separately.

**Evolution [code]**: book PROPFIND for `CS:getctag`, `supported-address-data`, `supported-report-set`
(`sync-collection`); privileges via `current-user-privilege-set`.

**Apple [doc/issue]**: sabre lists iOS requests on setup — principal: `principal-collection-set`,
`supported-report-set`, `resource-id`, `displayname`, `email-address-set`, `addressbook-home-set`,
`directory-gateway`; home: `add-member`, `bulk-requests`, `current-user-privilege-set`, `displayname`,
`max-image-size`, `max-resource-size`, `me-card`, `owner`, `push-transports`, `pushkey`, `quota-*`, `resource-id`,
`resourcetype`, `supported-report-set`, `sync-token`; book: `getctag`, `sync-token`; cards via
`addressbook-multiget` https://sabre.io/dav/clients/ios/ . macOS's parsed state (Sequoia, 2025) holds per
collection `privileges`, `resourceTypes`, `supportedReports`, `displayName`, `maxImageSize`, `maxResourceSize`,
`me-card`, `pushTransports`, `bulkRequests` — iCloud advertises `maxImageSize=229376`, `maxResourceSize=563200`
and only `sync-collection` + `addressbook-multiget` reports **[issue]**
https://github.com/sabre-io/Baikal/issues/1191#issuecomment-2933376247 . Old sabre note: macOS "requires …
`getctag`. Without it, it will simply not request any vcards" (10.6–10.10 era) **[doc]**; whether current macOS still
needs it with `sync-token` present: **unverified** — serve `CS:getctag` anyway (same value as the sync token).

**supported-address-data / version choice**

- RFC 6352 §6.2.2: absent property ⇒ only `text/vcard` 3.0. Attribute defaults: `content-type="text/vcard"`,
  `version="3.0"`.
- Thunderbird: stores vCard 4.0 locally; marks the book `carddav.vcard3` iff the server advertises 3.0 but not 4.0
  (also accepts the wrong element name `address-data` used by DAViCal); requests bare `<C:address-data/>`
  (no attributes) and parses whatever comes; when the property is absent it keeps the previous setting (SOGo
  workaround) **[code]**.
- DAVx5: if `supported-address-data` contains 4.0 it **chooses 4.0**: multiget requests
  `<address-data content-type="text/vcard" version="4.0">`, uploads `text/vcard;version=4.0`; otherwise it
  requests without a version (comment: "some vCard3-only servers may not understand it") **[code]**.
- Evolution: picks 4.0 if any `address-data-type` with `content-type="text/vcard"` has `version="4.0"`, else 3.0;
  multiget sends bare `<C:address-data/>` **[code]**.
- Apple: requests **3.0 explicitly** via `version` on `address-data` in REPORT bodies; a server (Stalwart) that
  ignored it and returned 4.0 caused partial syncs on macOS/iOS **[issue]**
  https://support.stalw.art/t/ios-contacts-and-mac-neo/480 (June 2026). Apple vCards written by the client are 3.0
  **[issue]** https://github.com/nextcloud/contacts/issues/213 .
- sabre advertises 3.0, 4.0 and `application/vcard+json` and converts on the fly, including GET `Accept`
  negotiation (default vCard 3) **[code]**.
- ⇒ Store one canonical form and **honour `content-type`/`version` on `address-data` and `Accept` on GET**.
  Advertise both 3.0 and 4.0 only if you can render both faithfully; serving 3.0 by default (no attributes) is
  what Thunderbird/Evolution/Apple expect.

**How many address books per account on Apple — the important one**

- macOS: sabre "OS X addressbook can only ever work with 1 address book" (10.6–10.10) **[doc]**. 2025 analysis on
  macOS Sequoia against Baikal, Radicale and Xandikos: "Using more than one address book does not work on macOS";
  macOS "never downloads additional address books from the CardDAV server in the first place"; contacts land in a
  "random" one; iCloud itself has exactly **one** address book (`/carddavhome/card/`) and models lists as groups
  **[issue]** https://github.com/sabre-io/Baikal/issues/1191#issuecomment-2933376247 ,
  https://github.com/Kozea/Radicale/issues/1796 . In that log macOS picked `another-book` and ignored
  `default-addressbook`, so which one wins is not predictable from the outside (**unverified** rule).
- iOS: evidence conflicts. iOS 18 user (Nextcloud, Dec 2024): "all addressbooks were synchronized to my iPhone"
  with no per-book selection **[issue]** https://help.nextcloud.com/t/sync-specific-addressbook-with-ios/212256 ;
  Infomaniak's KB: Apple Contacts does not support several books per account, only the default syncs **[doc]**
  https://www.infomaniak.com/en/support/faq/2181/understanding-the-synchronization-limits-for-contacts-calendar-macos-ios-apps .
  Even where iOS fetches several, it presents them as one account (contacts merged), never as separately selectable
  books. iOS 17/18 behaviour as separate "groups": **unverified**.
- ⇒ Design for **one address book per Apple account**. Ways: an app password / principal (or home URL) scoped to
  exactly one book, so each book is its own account on iOS/macOS; or publish a single book and represent subsets
  as Apple group vCards. Multi-book homes are fine for Thunderbird, DAVx5, Evolution (all list and let the user
  pick).

## 3. Sync REPORTs

| Client | sync-collection | addressbook-multiget | addressbook-query | Fallback |
|---|---|---|---|---|
| Thunderbird [code] | yes when a sync token is stored (`sync-level` 1); on 400 or `valid-sync-token` error → full resync (does **not** treat 403/412 as token errors despite the comment) | yes, batches of 500 (`carddav.multiget.batchSize`), bare `address-data` | **never** | PROPFIND Depth 1 etags (`updateAllFromServerV1`) |
| DAVx5 [code][doc] | yes iff `supported-report-set` lists it; invalid token (`valid-sync-token` error) → discard, full resync; 507 on the collection in the response = more changes pending | yes, with version 4.0 when advertised | **never** ("PROPFIND is more compatible") | PROPFIND Depth 1 `resourcetype`+`getetag` |
| Evolution [code] | yes if advertised and allowed; token must look like a URI (contains `:` or `/`) else ignored — `urn:elarion:sync:…` qualifies; on 412, or 403 whose body mentions `valid-sync-token`, retries without token | yes, batches of 100 | only in `list_existing_sync`: no `<filter>`, `Depth: 0`, partial `address-data` (`<C:prop name="VERSION"/><C:prop name="UID"/>`). The CardDAV backend overrides change detection with sync-collection/ctag+PROPFIND, so this path is rarely if ever hit (inferred) | `getctag` compare, then PROPFIND Depth 1 etags |
| Apple [doc/issue] | yes (iCloud advertises only sync-collection + multiget; macOS logs show sync-collection support recorded) | yes | for **directory gateway** search only (sabre docs) | getctag + PROPFIND (old) |

- Filtered `addressbook-query` (`prop-filter`/`param-filter`/`text-match`) from a mainstream client: only Apple's
  directory search **[doc]**. Thunderbird autocomplete and Evolution search run against the local cache (both
  are full-sync caches; Thunderbird `CardDAVDirectory extends SQLiteDirectory`) **[code]**.
- RFC 6352 §8.6 says the query request MUST carry a Depth header and the `filter` element is required, but sabre
  accepts a missing filter ("KDE sometimes does not include the filter element") **[code]**. sabre rejects
  Depth 0 on an address book collection; Evolution sends Depth 0 there. To be tolerant: treat missing filter as
  "match all" and consider treating `Depth: 0` on an address-book collection like Depth 1 for this report (or
  reject as sabre does; low impact).
- Partial `address-data` (`<C:prop name=…>`) is used by Evolution (above); RFC 6352 §8.4/§10.4.2. Return at least
  `BEGIN/END/VERSION/UID` plus requested props.

## 4. Read-only

- **Thunderbird [code]**: readOnly set only when `current-user-privilege-set` is present and contains `read`/`all`
  but none of `write`, `write-content`, `bind`, `all`; a book with neither read nor write is skipped; **missing
  property ⇒ treated as writable**. `AddrBookDirectory` then throws "Directory is read-only" on every mutation
  (UI disables editing). Read-only detection fixed in 91.2.0 https://www.thunderbird.net/en-US/thunderbird/91.2.0/releasenotes
  **[doc]**. Native CardDAV shipped in Thunderbird **91** (Aug 2021), not 102
  https://www.thunderbird.net/en-US/thunderbird/91.0/releasenotes/ **[doc]**.
- **DAVx5 [code]**: `privWriteContent = privilegeSet.mayWriteContent` (absent ⇒ writable); `readOnly() =
  forceReadOnly || !privWriteContent`; the Android address book is flagged read-only, and on sync
  `ReadOnlyPolicy` **reverts** local edits/deletions (resets ETag, forces full resync) instead of uploading.
- **Evolution [code]**: writable iff `current-user-privilege-set` contains `write`, `write-content` or `all`; if
  the privilege query fails, falls back to `Allow:` containing PUT/POST/DELETE. bugzilla 794874 "Use
  current-user-privilege-set to determine whether writeable" https://bugzilla.gnome.org/show_bug.cgi?id=794874 .
- **Apple**: macOS records `privileges` per collection (log above) **[issue]**. Behaviour evidence:
  - Read-only books make the account unselectable as iOS's default contacts account (Nextcloud's read-only
    "Recently contacted" book; workaround = disable that app) **[issue]** https://github.com/nextcloud/contacts/issues/1870 —
    still reported on iOS 26 (Apr 2026).
  - iOS (2020): editing/deleting a contact in a read-only book either shows an alert that the contact is
    read-only or creates a duplicate in the default (writable) account **[issue]**
    https://github.com/Kozea/Radicale/issues/1112 .
  - Same issue: iMessage "Update Contact" (Name & Photo Sharing) writes locally; the next sync PUT is refused
    (Radicale answered **401**), and "this error halts communication" until the account is toggled off/on.
  - iOS 7 era: edits on a read-only book reached the server (tine20 created duplicates) **[issue]**
    https://github.com/tine20/tine20/issues/4758 ; 2011 sabre thread: Apple kept re-sending denied PUTs "forever"
    https://groups.google.com/g/sabredav-discuss/c/K9fVdizwZdg .
  - ⇒ Apple mostly honours privileges today, but not completely. Return **403 with
    `<D:error><D:need-privileges>`** (never 401, which Apple reads as bad credentials) for PUT/DELETE/PROPPATCH.
    Whether iOS then discards the local change or retries: **unverified**. Changing the card's ETag (or the
    sync token) later forces a re-download that overwrites the local edit.
- Privilege-set gotcha: an **empty** `current-user-privilege-set` makes Thunderbird skip the book (SOGo bug)
  https://bugs.sogo.nu/view.php?id=6179 — always list `<D:privilege><D:read/></D:privilege>` (and
  `read-current-user-privilege-set`).

## 5. vCard content

- Apple: writes vCard 3.0; requests 3.0; **drops cards without `FN`** (Stalwart, June 2026: "Apple Contacts
  silently drops these cards") and was confused by 4.0-only params leaking into 3.0 output **[issue]**.
- Groups:
  - Apple: separate group vCard with `X-ADDRESSBOOKSERVER-KIND:group` and
    `X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:<member UID>` (vCard 3.0) **[issue]** https://github.com/nextcloud/contacts/issues/213 .
  - vCard 4.0: `KIND:group` + `MEMBER:urn:uuid:<UID>` (RFC 6350 §6.1.4, §6.6.5).
  - DAVx5: per-account setting, "Groups are separate vCards" (KIND/MEMBER on vCard 4 servers,
    X-ADDRESSBOOKSERVER-KIND/MEMBER on vCard 3) or "Groups are per-contact categories" (`CATEGORIES`)
    **[code][doc]**. Members are resolved by UID after all cards arrive.
  - Thunderbird: no CardDAV group support (`addMailList` throws NS_ERROR_NOT_IMPLEMENTED; no `KIND` handling
    in `VCardUtils`) **[code]** — a group vCard shows as an ordinary card (inferred).
  - Evolution: **unverified**.
  - ⇒ If groups are published: group vCards with both X-ADDRESSBOOKSERVER-* (in 3.0 output) / KIND+MEMBER (4.0),
    and optionally `CATEGORIES` for DAVx5 users on the categories method and for Thunderbird/Evolution.
- UID / resource name: DAVx5 keys local rows on the **resource file name** from the href, not UID; group membership
  uses UID **[code]**. Evolution hashes UIDs containing `/` for file names **[code]**. Keep href stable and
  `UID` stable and unique; `<uid>.vcf` is the common convention (DAVx5 `fileNameFromUid`).
- Photos: iCloud `max-image-size` 229376 bytes **[issue]**; DAVx5 resizes > 1 MB because of the Android provider
  limit; it fetches `PHOTO;VALUE=uri` URLs itself (authenticated to the same host) **[code][doc]**; Thunderbird
  `addPhotoTypes` when converting to 3.0 **[code]**. Inline base64 `PHOTO;ENCODING=b;TYPE=JPEG` (3.0) /
  `data:` URI (4.0) is safest; keep under ~224 KB.
- Content-Type: RFC uses `text/vcard`; Thunderbird PUTs `text/vcard`; DAVx5 `text/vcard;charset=utf-8` (3.0) and
  `text/vcard;version=4.0`; sabre rewrites `getcontenttype` to `text/x-vcard` only for UA "Thunderbird" (old SOGo
  connector / CardBook) **[code]**. No current client found that breaks on `text/vcard; charset=utf-8`.
- Clients that break on 4.0: Apple when it asked for 3.0 and got 4.0 **[issue]**; Thunderbird/DAVx5/Evolution
  handle 4.0 **[code]**.

## 6. Subscription / Outlook / Google

- No "vCard feed" subscription exists in mainstream clients analogous to `webcal:`; nothing found. Google Contacts
  imports `.csv`/`.vcf` **files** only https://support.google.com/contacts/answer/15147365 **[doc]**; no way to add
  an external CardDAV account in Google Contacts or Gmail (Android needs DAVx5). Outlook: no native CardDAV in
  classic, new Outlook, web or mobile; classic Outlook only via the third-party add-in Outlook CalDav Synchronizer
  (2007–2021/365 desktop, not new Outlook) https://github.com/aluxnimm/outlookcaldavsynchronizer **[doc]**. An
  on-demand `.vcf` export (one-shot import) is the only route for those users.

## 7. Directory gateway

- Not in RFC 6352. It is the expired draft `draft-daboo-carddav-directory-gateway-02` (Aug 2010)
  https://datatracker.ietf.org/doc/draft-daboo-carddav-directory-gateway/ : `CARD:directory-gateway` on the
  principal points to a collection with resourcetype `CARD:directory`.
- Used by Apple only: iOS/macOS treat it as a read-only, **search-only** directory (no browsing), queried with
  filtered `addressbook-query` **[doc]** https://sabre.io/dav/clients/osx-addressbook/ ; Apple CalendarServer
  implements it (`directorybackedaddressbook.py`). Thunderbird, DAVx5, Evolution: no reference in their code
  **[code]**. sabre has "very minimal" support.
- Verdict: not worth supporting for a published-contacts feature; it hides the contacts from browsing.

## 8. Implementer gotchas (checklist)

1. `DAV:` header with `addressbook` on OPTIONS of the **principal** (DAVx5 refuses the principal otherwise) and
   all CardDAV resources (RFC 6352 §6.1).
2. `/.well-known/carddav` answers **PROPFIND** (and GET) with a redirect (301/307) to the principal or DAV root,
   with authentication on the target; also answer `current-user-principal` on `/` (macOS and Thunderbird fall
   back to `PROPFIND /`).
3. Advertise `DAV:sync-collection` and `CARD:addressbook-multiget`/`addressbook-query` in `supported-report-set`
   (DAVx5 won't use sync-collection otherwise). Serve `sync-token` **and** `CS:getctag`.
4. Sync token invalid → `403` + `<D:error><D:valid-sync-token/></D:error>` (RFC 6578 §3.2 names the precondition
   but no status; sabre uses 403). Thunderbird recognises 400 **or** the `valid-sync-token` element (its comment:
   "400, 403 and 412 are all in use"); Evolution recognises 412, or 403 whose error text contains
   `valid-sync-token`; DAVx5 the element — 403 + element satisfies all three.
5. `current-user-privilege-set` never empty; include `read`.
6. Writes: `403` + `need-privileges`, never `401`.
7. One address book per Apple account; group vCards for subsets.
8. Honour `address-data` `version`/`content-type` attributes and partial `prop`; default 3.0; always emit `FN`,
   `N`, `UID`, `VERSION`.
9. Collections with trailing slash in hrefs, consistent between `addressbook-home-set`, Depth 1 listings and
   sync-collection responses. Whether iOS needs an absolute vs path-absolute `addressbook-home-set` href:
   **unverified** (sabre emits a path-absolute `LocalHref` and works with iOS).
10. macOS may crash without me-card support (old sabre note) — **unverified** for current macOS; a read-only server
    can return `404` for `CS:me-card` in the propstat.
11. Stalwart/Shiny.AspNet.CalDav-specific notes: Stalwart covered above; Shiny: **unverified** (nothing found).
