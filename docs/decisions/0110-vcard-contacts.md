---
type: Architecture Decision Record
title: "ADR-0110: vCard contacts — a fixed contact model over a lossless property list, Elarion's own reader and writer"
description: "Elarion.Contacts reads uploaded .vcf files of any version into a fixed contact model, a view over every property as read, with every contact kept and every error coded and located by line, and writes contacts as vCard 3.0 or 4.0, with its own bounded reader and writer on the content-line layer it shares with Elarion.Calendars, instead of a library and a generated record mapping."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T16:10:00Z }
status: draft
---
# ADR-0110: vCard contacts — a fixed contact model over a lossless property list, Elarion's own reader and writer

- Status: Proposed
- Date: 2026-11-26
- Related: [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node tier the defaults are sized for),
  [ADR-0039](0039-binary-file-responses.md) (the small-file tier),
  [ADR-0098](0098-streamed-files-for-http-only-endpoints.md) (streamed files),
  [ADR-0104](0104-previewable-commands.md) (previewable commands, which an import preview uses),
  [ADR-0105](0105-uploaded-files-are-handler-parameters.md) (uploaded files),
  [ADR-0108](0108-spreadsheet-records.md) (spreadsheet records, the reference file-format package),
  [ADR-0109](0109-icalendar-model-reader-and-writer.md) (iCalendar, whose design and content-line layer this shares),
  the research note [vCard libraries and producers](../research/2026-11-26-vcard-libraries-and-producers.md), and
  [spec 0005](../../specs/0005-vcard-contacts.md).

## Context

Applications import contacts from the files people export from their phone, their mail program or their contacts app,
and hand contacts out as `.vcf` files to be saved on a phone or in a mail program. A consuming application already
does the first by hand, over a vCard library: it decodes an upload by UTF-16 byte-order mark, then UTF-8, then
Windows-1252, skips contacts it already has, and keeps their pictures. Others need the same, and the
[file-format rule](../../.github/instructions/framework.instructions.md) asks every format package for one design
language, with `Elarion.Spreadsheets` (ADR-0108) as the reference and `Elarion.Calendars` (ADR-0109) as the model for a
format whose fields a standard fixes.

The research note found two things that shape the decision:

- **No library fits.** No .NET vCard library declares AOT or trimming compatibility. The only maintained one with
  broad coverage, FolkerKinzel.VCards, has no size, line, card or nesting limit, treats an `OutOfMemoryException` as
  the end of the file, drops malformed lines silently, reports no line numbers, parses embedded `AGENT` cards
  recursively without a depth limit, registers the code-page provider process-wide, and depends on four more packages
  of one maintainer.
- **Real files are messy but the mess is enumerable.** Android writes 2.1 with every byte of non-ASCII values
  quoted-printable and soft breaks that split UTF-8 sequences; Google escapes colons needlessly and writes a photo as a
  URL; Apple labels values with `itemN.X-ABLabel:_$!<Mobile>!$_` and marks a birthday without a year as year 1604;
  Outlook classic writes 2.1, one contact per file, with bare parameters; Thunderbird and Nextcloud mix 3.0 and 4.0 in
  one export. Each quirk is a few lines of a reader that knows it.

vCard differs from a spreadsheet in one way that matters for the design: its schema is fixed by the standard and
multi-valued. A contact has any number of phone numbers, e-mail addresses and postal addresses, each with types
(home, work, cell, fax, preferred) and possibly a custom label. A spreadsheet's columns are the application's; a
vCard's properties are RFC 6350's. In that it is like iCalendar, and RFC 5545 uses the same content-line grammar.

## Decision

`Elarion.Contacts` is an opt-in package that depends on `Elarion.Abstractions` and `Microsoft.Extensions` abstractions
and on no outside library. It is `IsAotCompatible`, writes to and reads from streams, and leaves the files to the
existing tiers. Registration is `AddElarionContacts(IConfiguration?, Action<ContactOptions>?)`, bound from the
configuration section `Contacts` through the configuration-binding generator, with limits and read defaults in one
options bag and per-call options of the same type, as `Elarion.Spreadsheets` and `Elarion.Calendars` do. A later
CardDAV package is `Elarion.CardDav`, over this one, as `Elarion.CalDav` is over `Elarion.Calendars`.

1. **A fixed contact model over a lossless property list, not a generated record mapping.** Each vCard read keeps
   every property as the file states it, in order, with its group, its parameters, its value as written and its line
   number (`ContactProperty`): the lossless layer, as the component tree is for calendars. The package's `Contact` is a
   view over that list: a formatted name, the structured name, nicknames, the kind (individual or organization),
   organization, department, title and role, lists of phone numbers, e-mail addresses, postal addresses and web
   addresses each with its types, a preferred flag and a custom label, a birthday and an anniversary that may lack a
   year, a note, categories, a photo, a UID, and the properties the view does not map, kept as they were read. The
   same model is read and written, and a contact converts to its property list and back, so an application can also
   prune or rewrite a card at the property level. The application maps a contact to its own entity in code, where the
   choice "which of three phone numbers is the mobile one" belongs. As the file-format rule says for a format whose
   fields a standard fixes, there is no `[<Area>Record]`: no column list to generate, the selection rules a flat
   mapping would need are the application's business logic, and a generator would add diagnostics and a validation
   root for no gain. Consequently there is no `[ValidationRoot]`, no DataAnnotations run and no diagnostic prefix; the
   application validates its own entity after mapping.
2. **Elarion reads and writes vCards itself, on a content-line layer shared with `Elarion.Calendars`.** The layer is
   internal and source-shared: one folder, `src/Shared/ContentLines/`, compiled into both packages, holding the
   content-line reader and writer, the decoding ladder of decision 3 and the code-page cache. It knows no property
   names; what differs between the formats is a rule set the package passes in. For vCard the rules are: unfolding
   removes all leading whitespace of a continuation line (iCalendar removes one character), groups (`item1.TEL`),
   bare 2.1 parameters, `CHARSET` per value, quoted-printable values with soft breaks with or without leading
   whitespace and with split escapes, and Base64 values that end at a blank line, at the next property line, or at a
   fold. Common to both are CRLF, LF and CR line ends (`\r\r\n` included), parameters (quoted values, repeated and
   comma-joined values, a space after `;`), RFC 6868 caret escapes, backslash escapes (lenient: an unknown escape
   reads as the character), names compared ignoring case, line numbers, and the line limit. The package reads 2.1, 3.0
   and 4.0, and mixed versions in one file.
3. **Text is decoded by the one ladder both text formats use.** A UTF-8 or UTF-16 byte-order mark, or a file that
   starts with `BEGIN` in UTF-16 without one, decides the whole file. Otherwise each logical line is decoded on its
   own: with its `CHARSET` parameter when it has one the runtime knows, else as UTF-8 when its bytes are valid UTF-8,
   else with the fallback encoding of the read options (`FallbackEncoding`, Windows-1252 by default), and the result
   says when the fallback was used (`UsedFallbackEncoding`). The package holds its own code-page encoding instances and
   never registers a provider process-wide. An unknown `CHARSET` is an error on the contact, and the value is decoded by
   the ladder. Line breaks in values read as `\n` on every platform, and invisible directional marks in phone numbers
   are removed.
4. **The read result keeps every contact.** Each vCard in the file becomes an entry with its index, the line number
   of its `BEGIN`, its version, its property list, its contact and its errors. A broken property (an unreadable date,
   invalid Base64, a photo over the limit, an unknown charset, a line over the limit, a line without a colon) costs
   only that property: the contact comes back without it, the property list keeps what was read, and the entry
   carries a located, coded error. An entry has no contact only when nothing usable is left: no formatted name, name,
   organization, e-mail address or phone number, or more properties than the limit. Its `HasValue` says whether it
   has one, and `Value` is the contact, the same names the calendar entries use; an error does not take the value
   away, because a contact with one unreadable birthday is still a contact (in a spreadsheet row, whose every cell
   belongs to the application's record, `IsValid` means a record and no errors). A card without `END:VCARD` at the
   end of the file is read, with `contact.unexpected_end`. So a preview lists every contact a person exported, and the
   application decides what an error means for it. The result's shape, codes and conversion follow ADR-0108.
5. **A file a user uploaded is input, not infrastructure.** As in ADR-0108: a file with no vCard in it, a file over
   the size limit and a file with too many contacts are file errors in the result; reading throws only on
   cancellation and on misconfiguration. Every error carries a stable `contact.*` code, an English default message and
   the location both line formats share: the item's index and the line of its `BEGIN`, the line, the property and its
   value as written, the expected kind and the limit; on top, the contact's display name and the charset. So a message
   reads "Contact 12 (Jane Doe), line 340: …". The application adds its own errors to an entry
   (`AddError(code, message, property?, lineNumber?)`, with its own code prefix, such as `import.duplicate`), located
   the same way. The result converts to an `AppError.Validation` keyed by a field path the handler names, worded by a
   formatter it may pass, and capped at 100 messages plus one that counts the rest.
6. **Values are read by documented rules.** Types from `TYPE` parameters, bare 2.1 parameters and Apple's
   `_$!<…>!$_` labels map to one set of flags (home, work, cell, voice, fax, pager, text, video, main); a preferred
   value is `PREF`, `TYPE=pref` or `PREF=1`; any other label is kept as the custom label. Birthdays and anniversaries
   read from `YYYYMMDD`, `YYYY-MM-DD`, a date-time (whose time is dropped), `--MMDD` and `--MM-DD`, and year 1604
   reads as no year, with or without `X-APPLE-OMIT-YEAR`. Anniversaries also come from `X-ANNIVERSARY`,
   `X-MS-ANNIVERSARY` and an `X-ABDATE` labelled as one. `KIND:org` and `X-ABSHOWAS:COMPANY` make an organization.
   `ADR` reads its seven components and ignores more. An `AGENT` value is never parsed as a vCard; it stays an
   additional property.
7. **Photos are decoded inline and never fetched.** An inline photo (2.1 and 3.0 Base64, a 4.0 `data:` URI) becomes
   bytes with a media type sniffed from the bytes (JPEG, PNG, GIF, WebP, BMP); bytes of no known image format and a
   photo over the size limit are dropped with an error. A photo given as a URL becomes a URI, only for `http` and
   `https`, and the package never requests it, so an upload cannot make the server fetch an address (server-side
   request forgery). The application decides whether to fetch it. The first photo of a contact is read; later ones
   stay additional properties.
8. **Writing is deterministic: vCard 3.0 by default, 4.0 on request.** UTF-8 without a byte-order mark, CRLF, folded
   at 75 octets without splitting a UTF-8 sequence, `TYPE=` parameters, custom labels as `itemN.X-ABLabel`, photos
   inline (`ENCODING=b;TYPE=…` in 3.0, a `data:` URI in 4.0), a birthday without a year as
   `X-APPLE-OMIT-YEAR=1604` in 3.0 and `--MMDD` in 4.0, an organization as `X-ABSHOWAS:COMPANY` in 3.0 and
   `KIND:org` in 4.0. `FN` is always written (derived from the name, the organization, an e-mail address or a phone
   number when the contact has none) and `N` always in 3.0. Additional properties are written as they were read. The
   same contacts give the same bytes. A contact the writer cannot represent (an additional property with an invalid
   name, a photo without bytes or URI) fails the write with a `ContactWriteException` naming the item's index and the
   property, because it is the application's error. 2.1 is never written. The writer has the shape of
   `SpreadsheetWorkbook` and `CalendarWriter`: an instance created over a stream that contacts are added to, and
   static `WriteAsync`, `ToFileAsync(contacts, fileName, …)`, `ToStreamedFile(contacts, fileName, …)` and
   `ToStreamedFile(fileName, write, …)`. Writing is forward-only to a stream, so an export of many contacts streams as
   an `ElarionStreamedFile`; a small one is an `ElarionFile`. The content-type constant is `text/vcard`, and files are
   served as `text/vcard; charset=utf-8`.
9. **Reading is bounded.** The input is read into memory up to a size limit and refused above it, so the memory a
   read takes is bounded by the limit and not by what the file claims. On top of the upload limits the handler
   declares, the read options bound the input (32 MiB), the contacts (10,000), the properties of one contact
   (1,000), one unfolded line other than a photo (1 MiB) and one decoded photo (1 MiB). A per-item limit costs at most
   that item and the read continues: a line or a photo over its limit costs its property, too many properties cost
   the contact. A file limit (the bytes, the number of contacts) stops the read with one error and keeps the contacts
   before it. Unfolding and decoding are linear in the input, and nothing recurses. The defaults are sized for the
   1–10 node tier and are options in the one options bag `AddElarionContacts` binds.

Out of scope: writing 2.1; jCard and xCard; parsing `AGENT`, `MEMBER`/`KIND:group` group cards, `RELATED`, `IMPP`,
`GEO`, `TZ`, `SOUND`, `LOGO` and `KEY` into the model (they stay additional properties); fetching photo URLs; resizing
or re-encoding photos; detecting duplicates within a file or against stored contacts; merging contacts; CardDAV.
What an import does with a contact stays the application's.

## Consequences

- An application adds the package and reads an uploaded `.vcf` in one call that returns every contact, its errors
  and the encoding used, from which it builds a preview (ideally as a previewable command, ADR-0104) and its import.
  It writes contacts with one call to a stream, an `ElarionFile` or an `ElarionStreamedFile`.
- Elarion owns a vCard reader and writer and, with them, the producers' quirks. They are known, listed in the research
  note, and pinned by tests built from real exports (the test files of ez-vcard, FolkerKinzel.VCards and AOSP, each
  license checked before use) and by a fuzz-style test of hostile input.
- No third-party runtime dependency, no global state, no reflection: the package is AOT- and trim-safe by
  construction, and a NativeAOT publish in the tests proves it.
- The application writes the mapping from `Contact` to its entity and back. That is a screen of plain code per
  application, and it is where its rules (which phone is the mobile, whether an organization-only contact is a
  company) live. An application whose own entity is also called `Contact` aliases one of the two.
- Every property survives a read and a write, mapped or not, so contacts exported from one program and written back
  by the application keep what the other program put there.
- Outlook classic imports only the first contact of a multi-contact file. The published page says so and shows a
  handler that writes one file per contact; the package does not offer an archive of single-contact files.
- The content-line layer is source-shared with `Elarion.Calendars`: a fix to folding or decoding lands in both
  packages in one change, and neither carries a public API for it.

## Alternatives considered

- **`[ContactRecord]` partial classes with a generated mapping.** An attribute per property would name a vCard field
  and a selection rule (`TEL` with `TYPE=CELL`, the first `EMAIL`, the preferred `ADR`). Rejected: vCard values are
  lists with overlapping types (`TYPE=CELL,WORK`), labels in three dialects and no natural flat shape, so the
  selection rules are the application's decisions, not mechanical mapping; and the generator, its diagnostics and a
  validation root would cost more than the plain mapping code they replace. Revisable: a record mapping can be added
  over the fixed model later without changing it.
- **A fixed model with only a bag of unmapped properties, no lossless list.** Simpler. Rejected: the entries would
  lose the order and line numbers of the mapped properties, a CardDAV package would need a second representation,
  and the calendar package already keeps a lossless tree, so the two would differ for no reason.
- **Wrapping FolkerKinzel.VCards behind an internal interface**, as ADR-0108 does with Sylvan.Data.Excel. Rejected:
  unlike Sylvan, it does not declare AOT compatibility, and closing its gaps for hostile input (limits, line numbers,
  no swallowed `OutOfMemoryException`, no recursion, no global registration) would need a pre-filter that re-parses
  most of the format anyway. Its test files remain useful as a corpus.
- **The content-line layer private to each package, or moved later.** Rejected: two copies of the same grammar drift,
  and "move it when the second ships" leaves the second package to discover the differences. A small internal package
  instead of a source-shared folder was rejected too: it would be a published package with no public API.
- **One encoding per file** (byte-order mark, then UTF-8 for the whole file, then the fallback), as the consuming
  application and FolkerKinzel's `AnsiFilter` do. Rejected for the per-line ladder: a 2.1 file mixes ASCII lines,
  `CHARSET`-tagged quoted-printable values and possibly untagged legacy bytes, and one invalid byte would push every
  UTF-8 value of the file through Windows-1252. The per-line ladder gives the same result for a uniform file.
- **Registering `CodePagesEncodingProvider` process-wide.** The documented way, but it changes
  `Encoding.GetEncoding(0)` for the whole host. Rejected for package-held instances.
- **A contact with an error is dropped**, as a spreadsheet row with an error has no valid record. Rejected: a contact
  with one unreadable birthday is still a contact the person wants imported; dropping it, or forcing the application
  to re-parse the raw text, loses data.
- **Calling them problems, not errors**, to say that they do not invalidate the contact. Rejected: `HasValue` already
  says that, and a second word for the same coded, located error would split the design language.
- **Writing 4.0 by default.** The current standard, but the AOSP importer rejects it and no Microsoft importer
  documents it. Rejected for 3.0 by default with 4.0 as an option.
- **Writing 2.1 for Outlook classic.** Its own format, but 2.1 needs quoted-printable and `CHARSET` for every
  non-ASCII value. Rejected; 3.0 is documented as an Outlook import format, and the limit that matters for Outlook is
  one contact per file, which the published page addresses.
- **Fetching photo URLs during the read.** Google exports photos only as URLs, so an import without them loses
  pictures. Rejected: a URL in an uploaded file would make the server request any address the uploader chooses. The
  application fetches with its own client, allow-list and limits if it wants them.
- **Exceptions for broken files**, or a result that keeps only the contacts without errors. Rejected for the
  reasons of ADR-0108: the person needs every error at once, and a preview needs every contact.
- **Streaming contacts one at a time instead of a result.** Would bound memory below the input size, but a preview
  needs them all, and the input limit already bounds memory at this tier. Rejected; revisable for a bulk-import
  pipeline beyond the tier.
- **The package named `Elarion.VCards`** after the format. Rejected for `Elarion.Contacts`, as `Elarion.Calendars` is
  named after what it holds, not after iCalendar; the protocol package over it is `Elarion.CardDav`.
