---
type: Specification
title: "Spec 0005: vCard contacts"
description: "What Elarion.Contacts must do: read uploaded .vcf files of any version and from any common producer into contacts, keeping every contact and locating every error, and write contacts as vCard 3.0 or 4.0, under NativeAOT."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T16:40:00Z }
status: stable
---
# Spec 0005: vCard contacts

This spec describes the new opt-in package `Elarion.Contacts`, which reads and writes vCard files. Why it is shaped
this way, and what was rejected, is in [ADR-0110](../docs/decisions/0110-vcard-contacts.md); what producers write is in
the research note [vCard libraries and producers](../docs/research/2026-11-26-vcard-libraries-and-producers.md). It
shares its design and its content-line layer with `Elarion.Calendars` ([spec 0004](0004-icalendar-files.md)); a later
CardDAV package is `Elarion.CardDav`, over this one.

## Intent

A developer reads a `.vcf` file a person uploaded — exported from an Android phone, an iPhone, a Mac, Google Contacts,
Outlook, Thunderbird or Nextcloud, in vCard 2.1, 3.0 or 4.0 — into `Contact` objects with one call, and maps
them to the application's own entities in plain code. The read keeps every contact, so the application can show a
preview before anything is saved; it reports every error with a code, its data and a location a person can find
(the contact, its name and the line), so the application words it in its own language; and it never throws because
of what the file contains. The developer also hands out contacts as a `.vcf` file that phones and mail programs
import. Hostile uploads are bounded, no address in a file is ever fetched, and the application still publishes with
NativeAOT.

## User scenarios

### Import contacts from an uploaded file (P1)

- Given a vCard 3.0 file exported from Apple Contacts with three contacts, when a handler reads the uploaded file's
  stream, then the result holds three entries, each with a contact holding the formatted name, the name parts, the
  organization, the phone numbers, e-mail addresses and postal addresses with their types, the birthday and the note.
- Given an Android export in vCard 2.1 whose non-ASCII names are UTF-8 quoted-printable with soft line breaks that
  split a multi-byte character, when it is read, then every name reads with its accents intact.
- Given an Outlook classic export in vCard 2.1 with bare parameters (`TEL;WORK;VOICE`) and a quoted-printable note
  over several lines, when it is read, then the work phone is typed work and voice and the note holds its line breaks
  as `\n`.
- Given a Google Contacts export with a `URL:http\://example.com` value and a phone number holding an invisible
  left-to-right mark, when it is read, then the URL reads `http://example.com` and the phone number without the mark.
- Given a Thunderbird or Nextcloud export that holds one 3.0 and one 4.0 card, when it is read, then both contacts
  read, each with its version.
- Given an Apple card with `item1.TEL:…` and `item1.X-ABLabel:_$!<Mobile>!$_`, and another with
  `item2.EMAIL:…` and `item2.X-ABLabel:Club`, when they are read, then the phone is typed cell and the e-mail address
  carries the custom label "Club".
- Given a card whose properties are written in lower case (`version:2.1`, `tel;cell:…`) and whose `BEGIN` is
  `BEGIN:vCard`, when it is read, then it reads like an upper-case one.
- Given a file with CRLF, LF and `\r\r\n` line ends, `#` comment lines and blank lines between cards, when it is read,
  then the contacts read and the comments and blank lines produce no error.
- Given a contact holding a property the model does not map (`X-SOCIALPROFILE`, `IMPP`, `X-ANDROID-CUSTOM`), when it
  is read, then the contact lists it among its additional properties with its group, its parameters and its value.
- Given a card of twelve properties, when it is read, then its entry lists all twelve in file order, each with its
  group, its parameters, its value as written and its line number, whether the contact maps it or not.

### Decode text in any encoding a producer used (P1)

- Given the same contacts saved as UTF-8 with and without a byte-order mark, as UTF-16 little-endian and big-endian
  with a byte-order mark, and as UTF-16 without one, when each is read, then the contacts are the same.
- Given a vCard 2.1 file in Windows-1252 without any `CHARSET` parameter, holding "Müller", when it is read, then the
  name reads "Müller" and the result says that the fallback encoding was used.
- Given a file whose values are UTF-8 except one value in Windows-1252, when it is read, then each value reads in its
  own encoding.
- Given an application that sets the fallback encoding to Windows-1250, when a Windows-1250 file without `CHARSET` is
  read, then its Czech names read correctly.
- Given a property with `CHARSET=Shift_JIS;ENCODING=QUOTED-PRINTABLE`, when it is read, then the value is decoded as
  Shift_JIS.
- Given a property with `CHARSET=x-unknown`, when it is read, then the value is decoded by the ladder and the entry
  carries a `contact.unknown_charset` error.

### Keep every contact and report every error (P1)

- Given a file of 50 contacts in which contact 12, "Jane Doe", has the birthday `BDAY:31.02.1980` on line 340, when it
  is read, then the result holds 50 entries, contact 12 holds every other property and no birthday, and its entry
  holds one error with the code `contact.invalid_value`, the property `BDAY`, line 340 and the expected kind "date".
- Given a contact whose photo is invalid Base64, when it is read, then the contact reads without a photo and its entry
  holds a `contact.invalid_value` error naming `PHOTO`.
- Given a card holding only `BEGIN`, `VERSION` and `END`, when it is read, then its entry has no contact and holds a
  `contact.empty` error.
- Given a card with an `N` and a phone number but no `FN`, as Android writes it, when it is read, then it has a
  contact whose display name comes from the name.
- Given a file whose last card lacks `END:VCARD`, when it is read, then that contact reads and its entry holds a
  `contact.unexpected_end` error.
- Given a line inside a card without a colon, when the file is read, then the line is skipped and the entry holds a
  `contact.malformed_line` error with its line number.
- Given a result whose entry 7 the handler finds already stored, when the handler adds an `import.duplicate` error
  to it, then the error carries the contact's index, line and display name like the package's own, the entry still
  has its contact, and the error appears in the result's errors and in its `AppError`.
- Given a result with errors, when the handler converts it to an `AppError` for its uploaded-file member `contacts`,
  then the caller receives a validation error keyed `contacts` whose messages name the contact, its display name and
  the line; given 300 errors, the error holds 100 messages and one that says 200 more exist; given a formatter, the
  messages use the application's wording.
- Given an upload that holds no vCard (a PDF, an empty file, a spreadsheet), when it is read, then the result holds no
  entries and one `contact.unreadable` error.

### Read names, dates and kinds as producers write them (P2)

- Given the birthdays `19800322`, `1980-03-22`, `1980-03-22T00:00:00Z` and `BDAY;value=date:1980-03-22`, when each is
  read, then each is 22 March 1980.
- Given the birthdays `--0322`, `--03-22`, `BDAY;X-APPLE-OMIT-YEAR=1604:1604-03-22` and `BDAY:1604-03-22`, when each
  is read, then each is 22 March without a year.
- Given an anniversary as `ANNIVERSARY`, `X-ANNIVERSARY`, `X-MS-ANNIVERSARY`, or `item3.X-ABDATE` labelled
  `_$!<Anniversary>!$_`, when each is read, then the contact's anniversary holds that date.
- Given a card with `KIND:org`, or with `X-ABSHOWAS:COMPANY`, when it is read, then the contact is an organization.

### Read and write group cards (P2)

- Given a vCard 3.0 card with `X-ADDRESSBOOKSERVER-KIND:group` and two `X-ADDRESSBOOKSERVER-MEMBER` values, or a 4.0
  card with `KIND:group` and two `MEMBER` values, when it is read, then the contact is a group whose members are the
  two references in order.
- Given a group with two members, when it is written as vCard 3.0, then the card holds `X-ADDRESSBOOKSERVER-KIND:group`,
  one `X-ADDRESSBOOKSERVER-MEMBER` per member and an `N` with the group's name; written as 4.0, it holds `KIND:group`
  and one `MEMBER` per member and none of Apple's properties.
- Given a 3.0 group card read and written as 4.0 and back as 3.0, then each card states the same group and members.
- Given a 4.0 card with `KIND:individual`, when it is written as 3.0, then the card holds no kind.
- Given a contact that is not a group but has members, when it is written, then the write fails naming `MEMBER`.
- Given `N:Doe;John;Richter,James;Mr.;Sr.`, when it is read, then the family name is "Doe", the given name "John",
  the additional names "Richter, James", the prefix "Mr." and the suffix "Sr.".
- Given a Google address with an eighth component, when it is read, then the seven standard components read and the
  eighth is ignored.
- Given `TEL;TYPE=CELL,WORK;TYPE=pref:…`, `TEL;PREF=1:…` and `TEL;CELL;PREF:…`, when each is read, then each phone is
  preferred, and the first is typed cell and work.

### Photos are decoded, bounded and never fetched (P2)

- Given photos as 2.1 Base64 ending at a blank line, 3.0 `ENCODING=b` without a `TYPE`, Base64 continuation lines
  indented by one, two or four spaces or not at all, and a 4.0 `data:image/png;base64,…` URI, when each is read, then
  the contact's photo holds the image bytes and the media type the bytes show.
- Given a photo declared `TYPE=JPEG` whose bytes are PNG, when it is read, then the media type is `image/png`.
- Given a photo of 3 MiB with the default limit, when it is read, then the contact has no photo and its entry holds a
  `contact.photo_too_large` error with the limit.
- Given `PHOTO:https://lh3.googleusercontent.com/…` and `PHOTO;VALUE=uri:file:///etc/passwd`, when each is read, then
  the first contact's photo holds the URI and no request is made, and the second contact has no photo and a
  `contact.invalid_value` error.

### Export contacts as a .vcf file (P1)

- Given three contacts, when a handler returns them as an `ElarionFile`, then the caller receives a `text/vcard` file
  in vCard 3.0 holding three cards that Apple Contacts, Google Contacts and Android import.
- Given the write options asking for vCard 4.0, when the contacts are written, then the file holds 4.0 cards.
- Given a contact with a photo, a birthday without a year, a custom label and an organization kind, when it is written
  as 3.0 and as 4.0, then each is written in the form that version's importers read, and reading the file back yields
  an equal contact.
- Given a contact for Outlook classic, which imports only the first card of a file, when the handler writes each
  contact to a file of its own, then each file holds one card.
- Given a contact without a formatted name, when it is written, then the card has an `FN` derived from its name, its
  organization, an e-mail address or a phone number.
- Given a note of 500 characters with umlauts and line breaks, when it is written, then no line is longer than 75
  octets, no multi-byte character is split across lines, and the note reads back unchanged.
- Given 100,000 contacts produced one at a time, when an HTTP-only handler returns them as a streamed file, then the
  first bytes reach the client before the last contact is produced, and the server's memory does not grow with the
  number of contacts.
- Given the same contacts written twice, when the two files are compared, then they are byte-identical.
- Given a contact read from a file with additional properties, when it is written, then the additional properties are
  written as they were read.
- Given a contact holding an additional property named `BAD NAME`, when it is written, then the write fails with an
  exception naming the contact and the property.

### Hostile uploads are bounded (P2)

- Given an upload larger than the read limit, when it is read, then the result holds one `contact.too_large` error and
  the reader held no more than the limit in memory.
- Given a file of 20,000 contacts with the default limit, when it is read, then the result holds the first 10,000
  entries and one `contact.too_many_contacts` error.
- Given a contact with 5,000 properties, when the file is read, then its entry has no contact and holds a
  `contact.too_many_properties` error, and the contacts after it read.
- Given a 5 MiB `NOTE` line, when the file is read, then the contact reads without the note and its entry holds a
  `contact.line_too_long` error.
- Given an `AGENT` property holding a vCard that holds another, nested a thousand times, when the file is read, then
  the read finishes in time linear in the file's size and the value stays an additional property.
- Given a file of 10 MiB made of one property folded into a million continuation lines, when it is read, then the read
  finishes in time linear in the file's size.

### Published with NativeAOT (P2)

- Given an application published with NativeAOT that reads and writes vCards, when it is published, then the package
  causes no trimming or AOT warning, and the published application reads back the contacts it wrote.

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

- FR-001: The feature MUST ship as the opt-in package `Elarion.Contacts`, which MUST NOT depend on a third-party
  library, ASP.NET Core, EF Core or `Elarion.Blobs`.
- FR-002: `AddElarionContacts(IConfiguration?, Action<ContactOptions>?)` MUST register the reader with one options bag,
  bound from the configuration section `Contacts`, whose read defaults per-call read options of the same type replace.
- FR-003: Reading MUST accept vCard 2.1, 3.0 and 4.0, and files that mix versions, and MUST report each entry's
  version.
- FR-004: Reading MUST match `BEGIN`, `END`, property names and parameter names ignoring case.
- FR-005: Reading MUST accept CRLF, LF and CR line ends, mixed in one file, and MUST skip blank lines and lines
  starting with `#`.
- FR-006: Reading MUST unfold a line followed by lines starting with a space or a tab, whatever the indentation, and
  MUST restore a multi-byte character split across a fold.
- FR-007: Reading MUST read a group prefix (`item1.TEL`), quoted parameter values, bare 2.1 parameters, repeated and
  comma-joined parameter values, and a space after `;`.
- FR-008: Reading MUST decode RFC 6868 caret escapes in parameter values.
- FR-009: Reading MUST unescape `\n`, `\N`, `\,`, `\;`, `\:` and `\\` in text values, and MUST read any other
  backslash sequence as the character after the backslash.
- FR-010: Reading MUST decode quoted-printable values, joining soft line breaks whether or not the next line starts
  with whitespace and whether or not the `=` is followed by spaces, including an escape or a multi-byte character
  split across a soft break.
- FR-011: Reading MUST decode Base64 values that end at a blank line, at the next line holding a property, or at the
  end of their folded lines.
- FR-012: Reading MUST decide the encoding of the whole file from a UTF-8 or UTF-16 byte-order mark, or from `BEGIN`
  encoded as UTF-16 without one.
- FR-013: Otherwise, reading MUST decode each logical line on its own: with its `CHARSET` parameter when the runtime
  knows that charset, else as UTF-8 when its bytes are valid UTF-8, else with the fallback encoding the read options
  name (`FallbackEncoding`), Windows-1252 by default. This is the decoding ladder `Elarion.Calendars` uses, which has
  no `CHARSET` step.
- FR-014: A `CHARSET` the runtime does not know MUST be a `contact.unknown_charset` error, and the value MUST be decoded
  as FR-013 says for a value without one.
- FR-015: The result MUST say whether the fallback encoding decoded any line (`UsedFallbackEncoding`), and which
  encoding a byte-order mark chose.
- FR-016: Reading MUST NOT change the process's registered encoding providers.
- FR-017: Line breaks in read values MUST be `\n` on every platform.
- FR-018: Reading MUST remove the Unicode directional formatting characters (U+200E, U+200F, U+202A to U+202E, U+2066
  to U+2069) from phone numbers.
- FR-019: Each vCard in the file MUST become an entry with its 1-based index, the line number of its `BEGIN`, its
  version, every property of the card in file order with its group, parameters, value as written and line number, its
  contact as `Value` when it has one with `HasValue` saying so, and its errors. An error MUST NOT by itself remove the
  contact.
- FR-020: A contact MUST hold the formatted name, the name (family, given, additional, prefix, suffix, several values
  of one part joined with `, `), nicknames, the kind, organization and department (the first two `ORG` components),
  title, role, phone numbers, e-mail addresses, postal addresses (post office box, extended address, street,
  locality, region, postal code, country) and web addresses, the birthday, the anniversary, the note, the categories,
  the photo, the UID, and every other property as an additional property with its group, parameters and value.
- FR-021: Each phone number, e-mail address, postal address and web address MUST carry its types as flags (home, work,
  cell, voice, fax, pager, text, video, main), whether it is preferred, and its custom label.
- FR-022: `TYPE` values, bare 2.1 parameters, and the Apple labels `_$!<Mobile>!$_`, `_$!<iPhone>!$_`,
  `_$!<Main>!$_`, `_$!<Home>!$_`, `_$!<Work>!$_`, `_$!<HomeFAX>!$_`, `_$!<WorkFAX>!$_`, `_$!<Pager>!$_` and
  `_$!<HomePage>!$_` MUST map to the flags; `PREF`, `TYPE=pref` and `PREF=1` MUST mark a value preferred; any other
  `X-ABLabel` of the value's group, with `_$!<` and `>!$_` removed, MUST be its custom label.
- FR-023: A birthday or anniversary MUST read from `YYYYMMDD`, `YYYY-MM-DD`, a date-time (dropping the time), `--MMDD`
  and `--MM-DD`, and year 1604 MUST read as a date without a year, with or without `X-APPLE-OMIT-YEAR`.
- FR-024: The anniversary MUST read from `ANNIVERSARY`, `X-ANNIVERSARY`, `X-MS-ANNIVERSARY`, and an `X-ABDATE` whose
  group's `X-ABLabel` is `_$!<Anniversary>!$_`, in that order of precedence.
- FR-025: `KIND:org` and `X-ABSHOWAS:COMPANY` MUST make the contact an organization; any other contact MUST be an
  individual.
- FR-026: An `ADR` with more than seven components MUST read the first seven and ignore the rest.
- FR-027: A contact MUST offer a display name: the formatted name, else the name's parts, else the organization, else
  the first e-mail address, else the first phone number.
- FR-028: An inline photo (Base64 or a `data:` URI) MUST read as its bytes with the media type its bytes show (JPEG,
  PNG, GIF, WebP or BMP), whatever the declared type.
- FR-029: A photo whose bytes show none of those formats MUST be dropped with a `contact.invalid_value` error.
- FR-030: A photo given as a URI MUST read as that URI when its scheme is `http` or `https`, and MUST otherwise be
  dropped with a `contact.invalid_value` error.
- FR-031: Reading MUST NOT request any URI a file contains.
- FR-032: Only the first `PHOTO` of a contact MUST be read into its photo; later ones MUST stay additional properties.
- FR-033: An `AGENT` value MUST NOT be parsed as a vCard.
- FR-034: A property value that does not read as its kind MUST be dropped with a `contact.invalid_value` error that
  carries the property and the expected kind, and MUST NOT remove the contact.
- FR-035: A line without a colon inside a card MUST be skipped with a `contact.malformed_line` error.
- FR-036: A card without a formatted name, name, organization, e-mail address or phone number MUST have no contact and
  a `contact.empty` error.
- FR-037: A card not closed by `END:VCARD` before the next `BEGIN:VCARD` or the end of the file MUST be read, with a
  `contact.unexpected_end` error.
- FR-038: Reading MUST return the entries and the errors together, and MUST throw only on cancellation and on
  misconfiguration, never because of what the file contains.
- FR-039: A file without a `BEGIN:VCARD` line MUST be exactly one `contact.unreadable` error and no entries.
- FR-040: Every error MUST carry a stable code, the data its wording needs, and an English default message.
- FR-041: The codes MUST be `contact.unreadable`, `contact.too_large`, `contact.too_many_contacts`,
  `contact.too_many_properties`, `contact.line_too_long`, `contact.photo_too_large`, `contact.invalid_value`,
  `contact.unknown_charset`, `contact.malformed_line`, `contact.empty` and `contact.unexpected_end`.
- FR-042: An error about a contact MUST carry the entry's index and the line of its `BEGIN`, the contact's display
  name when it has a contact, and, when it concerns a property, the property's name, its line and its value as
  written; an error about a value MUST carry the expected kind, an error about a limit the limit, and
  `contact.unknown_charset` the charset. These are the location fields `Elarion.Calendars` errors carry too.
- FR-043: Reading MUST report every error of every entry it reads.
- FR-044: The application MUST be able to add its own errors to an entry with its own code, and optionally a property
  and a line (`AddError(code, message, property?, lineNumber?)`), located like the package's own, and they MUST
  appear in the result's errors and in its `AppError`.
- FR-045: The result MUST convert to an `AppError.Validation` whose field errors are keyed by a field path the caller
  names, or the empty key when it names none, with at most 100 messages, each worded by the caller's formatter or else
  naming the contact, its display name and the line, and one closing message that counts the rest.
- FR-046: Reading MUST refuse with one `contact.too_large` error an input larger than the read limit, 32 MiB by default,
  without holding more than the limit in memory.
- FR-047: Reading MUST stop with one `contact.too_many_contacts` error after the maximum number of entries of the read
  options, 10,000 by default, keeping the entries before it.
- FR-048: A card with more properties than the read limit, 1,000 by default, MUST have no contact and a
  `contact.too_many_properties` error, and reading MUST continue with the next card.
- FR-049: An unfolded line longer than the read limit, 1 MiB by default, other than a photo, MUST be dropped with a
  `contact.line_too_long` error.
- FR-050: A photo whose decoded size exceeds the read limit, 1 MiB by default, MUST be dropped with a
  `contact.photo_too_large` error, without decoding more than the limit.
- FR-051: Reading MUST take time and memory linear in the size of the input.
- FR-052: Writing MUST produce vCard 3.0 by default and vCard 4.0 when the write options ask for it, and MUST NOT
  produce 2.1.
- FR-053: Writing MUST produce UTF-8 without a byte-order mark, CRLF line ends, and lines of at most 75 octets folded
  without splitting a UTF-8 sequence.
- FR-054: Writing MUST escape text values so that reading the file back yields the written values.
- FR-055: Writing MUST write types as `TYPE=` parameters, a preferred value as `TYPE=pref` in 3.0 and `PREF=1` in 4.0,
  and a custom label as an `X-ABLabel` property of the value's group.
- FR-056: Writing MUST write an inline photo as `ENCODING=b` with its type in 3.0 and as a `data:` URI in 4.0, and a
  photo URI as a URI value.
- FR-057: Writing MUST write a date without a year as `X-APPLE-OMIT-YEAR=1604` with year 1604 in 3.0, and as `--MMDD`
  in 4.0.
- FR-058: Writing MUST write an organization as `X-ABSHOWAS:COMPANY` in 3.0 and `KIND:org` in 4.0.
- FR-059: Writing MUST write an `FN` for every contact, from the display name of FR-027 when the contact has no
  formatted name, and an `N` for every contact in 3.0.
- FR-060: Writing MUST write additional properties as they were read.
- FR-061: A contact the writer cannot represent — an additional property whose name or parameter is not a valid vCard
  name, a photo with neither bytes nor URI — MUST fail the write with a `ContactWriteException` that carries the
  item's index (`ItemIndex`) and the property (`Property`).
- FR-062: Writing MUST be forward-only to a stream and MUST NOT hold more than one contact in memory.
- FR-063: Writing MUST be able to produce the file as an `ElarionFile` and as an `ElarionStreamedFile`, served as
  `text/vcard; charset=utf-8` with a file name the caller gives, through the shape `Elarion.Spreadsheets` and
  `Elarion.Calendars` use: an instance writer created over a stream that contacts are added to, and static
  `WriteAsync`, `ToFileAsync(contacts, fileName, …)`, `ToStreamedFile(contacts, fileName, …)` and
  `ToStreamedFile(fileName, write, …)`.
- FR-064: The same contacts MUST produce byte-identical files.
- FR-065: Every contact written, read back, MUST be equal to the contact written, except that a contact without a
  formatted name reads back with the one FR-059 wrote, and a line break in a value reads back as `\n`.
- FR-066: Reading and writing MUST NOT use reflection, so that an application published with NativeAOT gets no
  trimming or AOT warning from the package.
- FR-067: A contact MUST convert to its property list and back, and the writer MUST also write an entry's property
  list as it holds it, so a card read and written again keeps every property, mapped or not.
- FR-068: `KIND:group` and `X-ADDRESSBOOKSERVER-KIND:group`, in any case and either version, MUST make the contact a
  group (`ContactKind.Group`), and `KIND:individual` an individual.
- FR-069: Every `MEMBER` and `X-ADDRESSBOOKSERVER-MEMBER` value MUST be a member of the contact (`Contact.Members`), in
  file order; `ContactMember.FromUid(uid)` MUST build the `urn:uuid:` reference to a contact.
- FR-070: Writing MUST write a group as `X-ADDRESSBOOKSERVER-KIND:group` with one `X-ADDRESSBOOKSERVER-MEMBER` per
  member and an `N` whose family name is the group's display name in 3.0, and as `KIND:group` with one `MEMBER` per
  member in 4.0, with neither version holding the other's group properties.
- FR-071: Writing a contact that is not a group but has members MUST fail with a `ContactWriteException` naming
  `MEMBER`.

## Success criteria

- SC-001: The real exports of the research note's producers — Android, Google Contacts, Apple, Outlook classic,
  Thunderbird and Nextcloud, in every version they write — read with every contact and no error the file does not
  deserve.
- SC-002: A file written by the package imports without an error into Apple Contacts, Google Contacts and Android's
  contacts app, and its first contact into Outlook classic.
- SC-003: An application published with NativeAOT that reads and writes vCards gets zero trimming or AOT warnings from
  the package.
- SC-004: One read of a file with several broken contacts reports every error, each with a location a person can
  find.
- SC-005: Reading a hostile file of the maximum size finishes within twice the time of reading a well-formed file of
  that size, and holds no more memory than a small multiple of the size limit.
- SC-006: An application can word every error of an import in its own language without parsing a message.
- SC-007: An application that hand-wrote a vCard import with an encoding ladder and pictures can replace its parsing
  and decoding code with the package and keep its behavior.

## Out of scope

- Writing vCard 2.1; jCard (RFC 7095) and xCard.
- Mapping `RELATED`, `IMPP`, `GEO`, `TZ`, `SOUND`, `LOGO`, `KEY`, `LABEL`,
  `X-SOCIALPROFILE`, `X-ANDROID-CUSTOM` or phonetic names into the model; they stay additional properties.
- Fetching photo URIs, and resizing or re-encoding photos.
- Detecting duplicates within a file or against stored contacts, and merging contacts.
- An archive of single-contact files for Outlook classic; the application writes one file per contact.
- A generated record mapping (`[ContactRecord]`) and DataAnnotations validation of contacts.
- CardDAV.
- Enumerating the contacts of a file one at a time without collecting them.

## Decisions

- [ADR-0114](../docs/decisions/0114-contact-groups.md): group cards in the model (FR-068–FR-071).
- [ADR-0110](../docs/decisions/0110-vcard-contacts.md): the fixed contact model over a lossless property list, the own
  reader and writer, the per-line decoding ladder, the read result, photos, limits, the written versions.
- [ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md): the sibling format package whose entry,
  error, limit and writer shapes, decoding ladder and content-line layer this package shares.
- [ADR-0108](../docs/decisions/0108-spreadsheet-records.md): the reference file-format package whose result, codes,
  `ToAppError` and file tiers this package follows.
- [ADR-0098](../docs/decisions/0098-streamed-files-for-http-only-endpoints.md) and
  [ADR-0105](../docs/decisions/0105-uploaded-files-are-handler-parameters.md): the streamed-file and uploaded-file tiers
  an export and an import use.
