---
type: Plan
title: "Plan 0005: vCard contacts"
description: "How Elarion.Contacts of spec 0005 is built: project, public surface, content-line reader, decoding, contact mapping, writer, limits, tests and tasks."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T17:10:00Z }
status: draft
---
# Plan 0005: vCard contacts

How [spec 0005](0005-vcard-contacts.md) is built. The design and its rejected alternatives are in
[ADR-0110](../docs/decisions/0110-vcard-contacts.md); producer behavior and the library survey are in the research
note [vCard libraries and producers](../docs/research/2026-11-26-vcard-libraries-and-producers.md). Names below are the
intended ones and may move during review; the FR numbers are fixed. The package follows `Elarion.Spreadsheets`
(ADR-0108, [plan 0003](0003-spreadsheet-records.plan.md)) wherever ADR-0110 does not say otherwise.

## Project

| Project | Target | Holds |
| --- | --- | --- |
| `src/Elarion.Contacts` | `net10.0;net11.0`, `IsAotCompatible` | Model, reader, writer, options, DI registration |
| `src/Shared/ContentLines/` | compiled into `Elarion.Contacts` and `Elarion.Calendars` | The internal content-line reader and writer, the decoding ladder, the code-page cache |

References: `Elarion.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder` with the configuration-binding generator, as
`Elarion.Spreadsheets` and `Elarion.Typst` do. No third-party package, no generator project (FR-001). Code pages come
from `System.Text.Encoding.CodePages` in the shared framework.

## Public surface

```csharp
namespace Elarion.Contacts;

// Registration (FR-002)
services.AddElarionContacts(IConfiguration? configuration = null, Action<ContactOptions>? configure = null);  // TryAddSingleton<ContactReader>
public sealed class ContactOptions {             // section "Contacts" (SectionName)
    public ContactReadOptions ReadDefaults { get; set; } = new();
}
public sealed record ContactReadOptions {
    long MaxBytes = 32 MiB; int MaxContacts = 10_000; int MaxPropertiesPerContact = 1_000;
    int MaxLineBytes = 1 MiB; int MaxPhotoBytes = 1 MiB;
    string FallbackEncoding = "windows-1252";   // a name the runtime or the code-page provider knows; misconfiguration throws
}

// Model (FR-020–FR-027): sealed classes with init members and public constructors, value equality (FR-065)
public sealed class Contact {
    string? FormattedName; ContactName? Name; IReadOnlyList<string> Nicknames; ContactKind Kind;
    string? Organization; string? Department; string? Title; string? Role;
    IReadOnlyList<ContactPhone> Phones; IReadOnlyList<ContactEmail> Emails;
    IReadOnlyList<ContactAddress> Addresses; IReadOnlyList<ContactUrl> Urls;
    ContactDate? Birthday; ContactDate? Anniversary; string? Note; IReadOnlyList<string> Categories;
    ContactPhoto? Photo; string? Uid; IReadOnlyList<ContactProperty> AdditionalProperties;
    string DisplayName { get; }                                   // FR-027, computed
    IReadOnlyList<ContactProperty> ToProperties(ContactFormatVersion version);           // FR-067
    static bool TryFromProperties(IReadOnlyList<ContactProperty> properties, out Contact? contact,
        out IReadOnlyList<ContactError> errors);                                         // FR-067
}
public sealed record ContactName(string? Family, string? Given, string? Additional, string? Prefix, string? Suffix);
public enum ContactKind { Individual, Organization }
[Flags] public enum ContactTypes { None = 0, Home = 1, Work = 2, Cell = 4, Voice = 8, Fax = 16, Pager = 32, Text = 64, Video = 128, Main = 256 }
public sealed record ContactPhone(string Number)   { ContactTypes Types; bool Preferred; string? Label; }
public sealed record ContactEmail(string Address)  { ContactTypes Types; bool Preferred; string? Label; }
public sealed record ContactUrl(string Value)      { ContactTypes Types; bool Preferred; string? Label; }
public sealed record ContactAddress { string? PostOfficeBox, Extended, Street, Locality, Region, PostalCode, Country;
                                    ContactTypes Types; bool Preferred; string? Label; }
public readonly record struct ContactDate(int? Year, int Month, int Day);   // validated on construction
public sealed class ContactPhoto {                                          // exactly one of Data and Uri
    public static ContactPhoto FromData(ReadOnlyMemory<byte> data);         // media type sniffed (FR-028)
    public static ContactPhoto FromUri(Uri uri);                            // http/https only
    ReadOnlyMemory<byte>? Data; string? MediaType; Uri? Uri;
}
public sealed record ContactProperty(string? Group, string Name, IReadOnlyList<ContactParameter> Parameters, string Value) {
    int? LineNumber;                                                       // set when read (FR-019)
}
public sealed record ContactParameter(string Name, IReadOnlyList<string> Values);

// Reading (FR-003–FR-051)
public sealed class ContactReader {
    public ValueTask<ContactReadResult> ReadAsync(Stream source, ContactReadOptions? options = null, CancellationToken ct = default);
}
public sealed class ContactReadResult {
    public IReadOnlyList<ContactEntry> Entries { get; }
    public IReadOnlyList<ContactError> FileErrors { get; }   // unreadable, too large, too many contacts
    public IEnumerable<ContactError> Errors { get; }         // file errors, then every entry's, in order
    public IEnumerable<Contact> Contacts { get; }            // the values of the entries that have one
    public bool HasErrors { get; }
    public string? ByteOrderMarkEncoding { get; } public bool UsedFallbackEncoding { get; }   // FR-015
    public AppError ToAppError(string? fieldPath = null, Func<ContactError, string>? format = null, int maxMessages = 100);  // FR-045
}
public sealed class ContactEntry {                // the entry shape CalendarEntry<T> has
    public int Index { get; } public int LineNumber { get; } public string Version { get; }
    public IReadOnlyList<ContactProperty> Properties { get; }                 // lossless, file order (FR-019)
    public Contact? Value { get; }
    [MemberNotNullWhen(true, nameof(Value))] public bool HasValue { get; }    // an error does not clear it
    public IReadOnlyList<ContactError> Errors { get; }
    public void AddError(string code, string message, string? property = null, int? lineNumber = null);   // FR-044
}
public sealed class ContactError {
    public string Code { get; } public string Message { get; }
    // the location core both line formats share (CalendarError has the same members)
    public int? ItemIndex { get; } public int? ItemLineNumber { get; } public int? LineNumber { get; }
    public string? Property { get; } public string? Value { get; } public string? ExpectedKind { get; } public long? Limit { get; }
    // vCard extras
    public string? DisplayName { get; } public string? Charset { get; }
}
public static class ContactErrorCodes { Unreadable = "contact.unreadable", TooLarge = "contact.too_large", ... }   // FR-041, no Validation

// Writing (FR-052–FR-065, FR-067): one type, the shape of SpreadsheetWorkbook and CalendarWriter
public enum ContactFormatVersion { V3_0, V4_0 }
public sealed record ContactWriteOptions { ContactFormatVersion Version = ContactFormatVersion.V3_0; }
public sealed class ContactWriter : IAsyncDisposable {
    public const string ContentType = "text/vcard";          // served as "text/vcard; charset=utf-8"
    public static ContactWriter Create(Stream destination, ContactWriteOptions? options = null);
    public ValueTask AddAsync(Contact contact, CancellationToken ct = default);
    public ValueTask AddAsync(IReadOnlyList<ContactProperty> card, CancellationToken ct = default);   // FR-067, as held
    public static ValueTask WriteAsync(Stream destination, IEnumerable<Contact> | IAsyncEnumerable<Contact> contacts,
        ContactWriteOptions? options = null, CancellationToken ct = default);
    public static ValueTask<ElarionFile> ToFileAsync(IEnumerable<Contact> | IAsyncEnumerable<Contact> contacts,
        string fileName, ContactWriteOptions? options = null, CancellationToken ct = default);
    public static ElarionStreamedFile ToStreamedFile(IAsyncEnumerable<Contact> contacts, string fileName,
        ContactWriteOptions? options = null);
    public static ElarionStreamedFile ToStreamedFile(string fileName, Func<ContactWriter, CancellationToken, ValueTask> write,
        ContactWriteOptions? options = null);
}
public sealed class ContactWriteException : Exception { int? ItemIndex; string? Property; }   // FR-061
```

- `ContactReader` is registered as a singleton (`TryAddSingleton`), as `CalendarReader` is: it holds no per-request
  state and reads its defaults from `IOptions<ContactOptions>`, bound from the section `Contacts`. The writer needs no
  service.
- The model type is `Contact`, the glossary's term. An application whose own entity is also `Contact` aliases one of
  the two (`using VContact = Elarion.Contacts.Contact;`).
- `ContactEntry.AddError` is the one mutation, as `SpreadsheetRow<T>.AddError`: the handler adds its own errors
  before its preview or `ToAppError`. It does not remove the contact; a handler that wants to skip a contact decides
  that itself.
- The error codes live in the package, not in `ErrorCodes`, for the reason of plan 0003.
- `ToAppError`'s default message: `Contact {index} ({displayName}), line {line}: {message}`, or without the name when
  the entry has no contact, or `{message}` for a file error.
- `HasValue` is not `IsValid`: in `Elarion.Spreadsheets` `IsValid` means a record and no errors; here an entry with
  errors keeps its contact, so the flag says only whether there is one. Application codes use their own prefix
  (`import.duplicate`), never `contact.`.
- The equality of the model is structural (lists compared by element, `ReadOnlyMemory<byte>` by content), so a test
  and an application can compare a contact read back with the one written.

## Reading pipeline

All internal. Steps 1–4 are the shared content-line layer in `src/Shared/ContentLines/` (see "Shared with
iCalendar"), run with the vCard rule set; steps 5–8 are in `Elarion.Contacts/Reading`. One pass over the input, no
recursion, no regular expressions on unbounded input.

1. **Buffer** (FR-046): read the stream into a pooled buffer, refusing with `contact.too_large` as soon as
   `MaxBytes + 1` bytes arrived. Cancellation is checked per chunk.
2. **File encoding** (FR-012): a UTF-8 BOM is skipped; a UTF-16 LE or BE BOM, or `B\0E\0` / `\0B\0E` before the first
   line break, transcodes the buffer to UTF-8 once (bounded: UTF-16 to UTF-8 at most grows by half) and marks every
   line as "already decoded". Otherwise the buffer stays bytes and each logical line is decoded on its own (step 4).
3. **Content lines** (`ContentLineReader`, FR-004–FR-011): an iterator over the buffer that yields logical lines as
   spans plus the physical line number of their first line. Line ends CR, LF, CRLF and `\r\r\n` all end one line.
   Unfolding removes every leading space and tab of a continuation line (the vCard rule; iCalendar's removes one) and
   appends continuation segments into one growing pooled buffer per logical line (linear, FR-051) and stops
   appending past `MaxLineBytes` (FR-049) while still consuming the continuation, except for a photo, where the limit is
   `MaxPhotoBytes × 4 / 3` plus folding slack (FR-050). Quoted-printable soft breaks join the next physical line
   whatever it starts with (FR-010). A 2.1 Base64 value without folding whitespace collects lines until a blank line or
   a line that parses as `name[;params]:` (FR-011). Blank lines and `#` lines between properties are skipped (FR-005).
   The tokenizer splits group, name, parameters (quoted values, bare 2.1 parameters, comma lists, spaces after `;`,
   RFC 6868 carets, FR-007, FR-008) and the raw value bytes.
4. **Decoding** (`ContentLineDecoder` in the shared layer, `ValueDecoder` here, FR-013–FR-017):
   `ENCODING=QUOTED-PRINTABLE` → bytes (an invalid escape keeps its characters); `ENCODING=b|BASE64` → bytes for binary
   properties. Text bytes of one logical line → string by the shared ladder: known `CHARSET` (a step the vCard rule set
   turns on) → that encoding; else strict UTF-8 (`UTF8Encoding(false, throwOnInvalidBytes: true)` via `Utf8.IsValid` first, no
   exception on the hot path); else `FallbackEncoding`, and the result's `UsedFallbackEncoding` is set. Encodings come
   from the shared code-page cache: built-in ones from
   `Encoding.GetEncoding`, others from `CodePagesEncodingProvider.Instance.GetEncoding(name)`, never
   `Encoding.RegisterProvider` (FR-016). A fallback name that resolves to nothing throws `InvalidOperationException` at
   registration or at the call (misconfiguration). Then backslash unescaping (FR-009) and line-break normalization to
   `\n` (FR-017).
5. **Cards** (FR-019, FR-035–FR-037, FR-047, FR-048): a state machine over the lines: `BEGIN:VCARD` opens an entry
   (closing an unterminated one with `contact.unexpected_end`), `END:VCARD` closes it, a line without `:` is
   `contact.malformed_line`, lines outside a card are ignored. A card counts its properties and, past the limit, keeps
   consuming to its `END` and ends without a contact (a per-item limit: the read continues). The entry limit and the
   byte limit are file limits and stop the read. Every property read is added to the entry's lossless list with its
   line number before mapping, including the ones mapping later drops with an error.
6. **Mapping** (`ContactMapper`, FR-020–FR-034): properties of one card are collected first (by group, for
   `X-ABLabel`, `X-ABDATE` and Apple's labels), then mapped. The version comes from `VERSION` (default 3.0 rules when
   absent; 2.1 rules enable bare parameters and Base64 to a blank line). Type mapping from one table: `TYPE` values,
   bare 2.1 parameters, `PREF`/`pref`/`PREF=1`, Apple labels (FR-021, FR-022); unknown `TYPE` values (`INTERNET`,
   `X-…`, `waid=`) are ignored for the flags; they stay in the entry's property list, and among the contact's
   additional properties only when the whole property is not mapped.
   Dates by one small parser (FR-023); 1604 → no year. Phone numbers lose directional marks (FR-018). `ORG` splits on
   unescaped `;` (FR-020), `N` and `ADR` likewise, components with unescaped `,` joined by `, `. A value that fails its
   parser becomes `contact.invalid_value` with the kind (`date`, `photo`, `uri`) and the property is dropped (FR-034).
   `AGENT` and everything else the view does not map stay `ContactProperty` values with the decoded, still-escaped
   value text (FR-033) in `AdditionalProperties`, so the writer can write them back unchanged (FR-060). `TryFromProperties`
   is this step on its own, so a property list built or pruned in code maps the same way (FR-067).
7. **Photos** (`PhotoReader`, FR-028–FR-032, FR-050): Base64 decoded into a buffer bounded by `MaxPhotoBytes` with
   `Base64.DecodeFromUtf8` in chunks (whitespace inside is skipped); a `data:` URI parsed by hand (media type, optional
   `;base64`); the bytes sniffed by magic number (`FF D8 FF`, `89 50 4E 47`, `GIF8`, `RIFF….WEBP`, `BM`). A URI value
   (4.0 `PHOTO:https://…`, 3.0 `VALUE=uri`, or a value that parses as an absolute `http`/`https` URI, as Google writes
   it) becomes `Uri`; nothing is requested (FR-031).
8. **Result**: entries in file order; errors carry the entry's display name, computed once the card is mapped.

## Writer

`Elarion.Contacts/Writing`, forward-only into a pooled UTF-8 buffer flushed with `WriteAsync` per contact (FR-062).

- Property order per card: `BEGIN`, `VERSION`, `KIND`/`X-ABSHOWAS`, `FN`, `N`, `NICKNAME`, `ORG`, `TITLE`, `ROLE`,
  `TEL`, `EMAIL`, `ADR`, `URL`, `BDAY`, `ANNIVERSARY` (4.0) or `X-ANNIVERSARY` and `item.X-ABDATE` (3.0), `NOTE`,
  `CATEGORIES`, `PHOTO`, `UID`, additional properties, `END` (FR-064).
- Labels: values with a custom label get a group `itemN` (N counting per card) and an `itemN.X-ABLabel` line
  (FR-055). 3.0 types as `TYPE=CELL,VOICE`-style lists in upper case, `TYPE=pref` for preferred; 4.0 types in lower
  case and `PREF=1`.
- Escaping per RFC 6350 §3.4 and RFC 2426 (FR-054); parameters with `:`, `;` or `,` are quoted; RFC 6868 carets in 4.0
  parameters.
- Folding (FR-053): count octets, break before 75, never inside a UTF-8 sequence, continuation starts with one space.
- Dates (FR-057), kinds (FR-058), `FN`/`N` (FR-059) as ADR-0110 decision 8 says. Photos (FR-056): 3.0
  `PHOTO;ENCODING=b;TYPE=JPEG:` with the sniffed subtype, 4.0 `PHOTO:data:image/jpeg;base64,…`, URI photos as
  `PHOTO;VALUE=uri:` (3.0) or `PHOTO:` (4.0).
- Additional properties are validated (name and parameter names `[A-Za-z0-9-]+`, a value without CR or LF) and written as
  stored (FR-060, FR-061).
- `AddAsync(card)` writes a property list in the order it holds, through the same validation (FR-067).
- `ToFileAsync` writes to a `MemoryStream` and returns an `ElarionFile` with `text/vcard; charset=utf-8`;
  `ToStreamedFile` wraps `ElarionStreamedFile.FromWriter`, and the delegate overload closes what was written when the
  delegate throws, as `SpreadsheetWorkbook` does (FR-063). Content lines are written by the shared content-line
  writer (folding on rune boundaries).

## Tests

All in `tests/Elarion.Tests/Contacts/`, named after the acceptance scenarios.

- **Corpus** under `Contacts/Fixtures/`: real exports from the producers of the research note, taken from the ez-vcard,
  AOSP and FolkerKinzel.VCards test files (task 1 checks each license and records the origin in a `README.md` next to
  them), plus files saved by hand from Apple Contacts, Google Contacts and Android where none exists (SC-001).
- **Encodings** built in code: UTF-8 with and without BOM, UTF-16 LE/BE with and without BOM, Windows-1252 without
  `CHARSET`, mixed values, Windows-1250 with the fallback option, Shift_JIS quoted-printable, an unknown charset.
- **Round trips** (FR-065): a generated set of contacts covering every member, written as 3.0 and 4.0 and read back,
  compared by equality; byte identity of two writes (FR-064); every written line ≤ 75 octets and valid UTF-8 after
  unfolding (FR-053).
- **Hostile input** (FR-046–FR-051, SC-005): oversized input over a non-seekable stream that counts bytes read; 20,000
  cards; a card with 5,000 properties; a 5 MiB line; a 3 MiB photo; `AGENT` nested a thousand times; a million
  continuation lines; a million `=` soft breaks; random bytes and random mutations of the corpus (a seeded loop, no
  exception other than cancellation, time bounded per input).
- **`ToAppError`** and **application errors** (FR-044, FR-045).
- **No global registration** (FR-016): `Encoding.GetEncoding(1252)` still throws after a read that used the fallback,
  in a test that runs alone (the test project must not register the provider elsewhere; verified in task 1).
- **AOT** (FR-066, SC-003): the `EdgeTelemetry` sample, already published with NativeAOT in CI, gains an
  operator-contacts export and import; CI fails on any IL2xxx/IL3xxx warning.
- **Benchmarks** in `tests/Elarion.Benchmarks`: reading a 10,000-contact file, writing 100,000 contacts to a null
  stream (SC-005, FR-062).
- SC-002 (importing written files into Apple, Google, Android and Outlook classic) is a manual check recorded in the
  pull request.

## Documentation

- `docs/capabilities/contacts.mdx` (and `meta.json`): reading an uploaded `.vcf` in a handler with `[UploadLimits]`
  (`FileExtensions = [".vcf", ".vcard"]`), mapping to an entity, a preview with errors, export as a file and as a
  streamed file, one file per contact for Outlook classic, the photo-URL caution.
- `docs/reference/packages.mdx`, `configuration.mdx` (`ContactOptions`), the error-code listing if one exists for
  package codes; `CHANGELOG.md`; the glossary term *contact* (added with the ADR).
- The file-format rule in `.github/instructions/framework.instructions.md` already says when a fixed model replaces
  `[<Area>Record]`, what `HasValue` means, how limits cost items and that text formats share one decoding ladder and
  content-line layer; the capability page links it.

## Tasks

1. **Project, model, options, registration, fixtures** — FR-001, FR-002, FR-020, FR-021, FR-027; collect and license-check
   the corpus.
2. **Content-line reader and decoding** — FR-003–FR-017, FR-046, FR-049, FR-051, in `src/Shared/ContentLines/`
   (created here, or taken over from `Elarion.Calendars` if it ships first).
3. **Cards, mapping and the result** — FR-018–FR-026, FR-033–FR-045, FR-047, FR-048.
4. **Photos** — FR-028–FR-032, FR-050.
5. **Writer and file tiers** — FR-052–FR-065, FR-067.
6. **Hostile-input suite, AOT sample, CI publish, benchmarks** — FR-066, SC-003, SC-005.
7. **Documentation** — capability page, references, changelog.

Task 2 comes first; 3 and 5 are independent after 2 (5 needs only the model of 1); 4 follows 3; 6 and 7 close.

## Shared with iCalendar

`Elarion.Calendars` (ADR-0109, spec 0004) uses the same content-line grammar and the same decoding ladder. Both
packages compile one internal, source-shared folder, `src/Shared/ContentLines/`, linked into each project the way the
generator projects already link shared files from `src/Elarion.Generators` (`<Compile Include="..\Shared\ContentLines\**\*.cs"
LinkBase="ContentLines" />`); it is no project and no package, and its types are `internal` in namespace
`Elarion.ContentLines`. It holds:

- the content-line reader: line ends, unfolding, the line limit, line numbers, the tokenizer (name, group,
  parameters with quoting and RFC 6868, raw value), escaping helpers;
- the content-line writer: escaping, parameter quoting, folding at 75 octets on rune boundaries, CRLF;
- the decoding ladder (byte-order mark and BOM-less UTF-16 sniff for the file; per logical line `CHARSET` where the
  grammar has it, strict UTF-8, `FallbackEncoding`), with the "fallback used" flag;
- the code-page cache (`CodePagesEncodingProvider.Instance` instances, never registered).

The layer knows no property names. A rule set passed in by each package holds what differs: how much indentation a
continuation line loses (vCard: all leading whitespace; iCalendar: one character), whether groups, bare parameters,
`CHARSET`, quoted-printable soft breaks and Base64 ending at a blank line apply (vCard only), and the limits. The
first of the two packages to be implemented creates the folder; the second links it. Tests of the layer live in
`tests/Elarion.Tests/ContentLines/` and run against both rule sets.

## Risks

- **The corpus may hide quirks no fixture shows** (Outlook's non-ASCII output, UTF-16 files, a missing `END`): the
  research found no primary evidence for them. Mitigation: the per-line ladder and the defensive rules, and the
  consuming application's own files as a check before release, anonymized.
- **Sniffing may disagree with what a producer meant** (a HEIC photo from a newer iPhone): it is dropped with an error
  today; adding a format is a table entry.
- **Equality of the model** must stay structural as members are added; a test enumerates the members to catch a
  forgotten one.
