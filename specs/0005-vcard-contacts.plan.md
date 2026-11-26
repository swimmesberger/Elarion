---
type: Plan
title: "Plan 0005: vCard contacts"
description: "How Elarion.VCards of spec 0005 is built: project, public surface, content-line reader, decoding, contact mapping, writer, limits, tests and tasks."
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
| `src/Elarion.VCards` | `net10.0;net11.0`, `IsAotCompatible` | Model, reader, writer, options, DI registration |

References: `Elarion.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder` with the configuration-binding generator, as
`Elarion.Spreadsheets` and `Elarion.Typst` do. No third-party package, no generator project (FR-001). Code pages come
from `System.Text.Encoding.CodePages` in the shared framework.

## Public surface

```csharp
namespace Elarion.VCards;

// Registration (FR-002)
services.AddElarionVCards(IConfiguration? configuration = null, Action<VCardOptions>? configure = null);
public sealed class VCardOptions { public VCardReadOptions ReadDefaults { get; set; } = new(); }
public sealed record VCardReadOptions {
    long MaxBytes = 32 MiB; int MaxContacts = 10_000; int MaxPropertiesPerContact = 1_000;
    int MaxLineBytes = 1 MiB; int MaxPhotoBytes = 1 MiB;
    string FallbackEncoding = "windows-1252";   // a name the runtime or the code-page provider knows; misconfiguration throws
}

// Model (FR-020–FR-027): sealed classes with init members and public constructors, value equality (FR-065)
public sealed class VCardContact {
    string? FormattedName; VCardName? Name; IReadOnlyList<string> Nicknames; VCardKind Kind;
    string? Organization; string? Department; string? Title; string? Role;
    IReadOnlyList<VCardPhone> Phones; IReadOnlyList<VCardEmail> Emails;
    IReadOnlyList<VCardAddress> Addresses; IReadOnlyList<VCardUrl> Urls;
    VCardDate? Birthday; VCardDate? Anniversary; string? Note; IReadOnlyList<string> Categories;
    VCardPhoto? Photo; string? Uid; IReadOnlyList<VCardProperty> UnmappedProperties;
    string DisplayName { get; }                                   // FR-027, computed
}
public sealed record VCardName(string? Family, string? Given, string? Additional, string? Prefix, string? Suffix);
public enum VCardKind { Individual, Organization }
[Flags] public enum VCardTypes { None = 0, Home = 1, Work = 2, Cell = 4, Voice = 8, Fax = 16, Pager = 32, Text = 64, Video = 128, Main = 256 }
public sealed record VCardPhone(string Number)   { VCardTypes Types; bool Preferred; string? Label; }
public sealed record VCardEmail(string Address)  { VCardTypes Types; bool Preferred; string? Label; }
public sealed record VCardUrl(string Value)      { VCardTypes Types; bool Preferred; string? Label; }
public sealed record VCardAddress { string? PostOfficeBox, Extended, Street, Locality, Region, PostalCode, Country;
                                    VCardTypes Types; bool Preferred; string? Label; }
public readonly record struct VCardDate(int? Year, int Month, int Day);   // validated on construction
public sealed class VCardPhoto {                                          // exactly one of Data and Uri
    public static VCardPhoto FromData(ReadOnlyMemory<byte> data);         // media type sniffed (FR-028)
    public static VCardPhoto FromUri(Uri uri);                            // http/https only
    ReadOnlyMemory<byte>? Data; string? MediaType; Uri? Uri;
}
public sealed record VCardProperty(string? Group, string Name, IReadOnlyList<VCardParameter> Parameters, string Value);
public sealed record VCardParameter(string Name, IReadOnlyList<string> Values);

// Reading (FR-003–FR-051)
public sealed class VCardReader {
    public ValueTask<VCardReadResult> ReadAsync(Stream source, VCardReadOptions? options = null, CancellationToken ct = default);
}
public sealed class VCardReadResult {
    public IReadOnlyList<VCardEntry> Entries { get; }
    public IReadOnlyList<VCardProblem> FileErrors { get; }            // unreadable, too large, too many contacts
    public IEnumerable<VCardProblem> Problems { get; }                // file errors, then every entry's, in order
    public IEnumerable<VCardContact> Contacts { get; }                // entries that have a contact
    public bool HasProblems { get; }
    public string? ByteOrderMarkEncoding { get; } public bool UsedFallbackEncoding { get; }   // FR-015
    public AppError ToAppError(string? fieldPath = null, Func<VCardProblem, string>? format = null, int maxMessages = 100);  // FR-045
}
public sealed class VCardEntry {
    public int Index { get; } public int LineNumber { get; } public string Version { get; }
    public VCardContact? Contact { get; }
    [MemberNotNullWhen(true, nameof(Contact))] public bool HasContact { get; }
    public IReadOnlyList<VCardProblem> Problems { get; }
    public void AddProblem(string code, string message, string? property = null, int? lineNumber = null);   // FR-044
}
public sealed class VCardProblem {
    public string Code { get; } public string Message { get; }
    public int? EntryIndex { get; } public int? EntryLineNumber { get; } public string? DisplayName { get; }
    public string? Property { get; } public int? LineNumber { get; }
    public string? ExpectedKind { get; } public long? Limit { get; } public string? Charset { get; }
}
public static class VCardProblemCodes { Unreadable = "vcard.unreadable", TooLarge = "vcard.too_large", ... }   // FR-041

// Writing (FR-052–FR-065)
public enum VCardVersion { V3_0, V4_0 }
public sealed record VCardWriteOptions { VCardVersion Version = VCardVersion.V3_0; }
public static class VCardWriter {
    public const string ContentType = "text/vcard";
    public static ValueTask WriteAsync(Stream destination, IEnumerable<VCardContact> | IAsyncEnumerable<VCardContact> contacts,
        VCardWriteOptions? options = null, CancellationToken ct = default);
    public static ValueTask<ElarionFile> ToFileAsync(IEnumerable<VCardContact> | IAsyncEnumerable<VCardContact> contacts,
        string fileName, VCardWriteOptions? options = null, CancellationToken ct = default);
    public static ElarionStreamedFile ToStreamedFile(IAsyncEnumerable<VCardContact> contacts, string fileName,
        VCardWriteOptions? options = null);
}
public sealed class VCardWriteException : Exception { int ContactIndex; string? Property; }   // FR-061
```

- `VCardReader` is a service only because it reads its defaults from `IOptions<VCardOptions>`; the writer needs none
  and is static, like `SpreadsheetWorkbook`'s shortcuts.
- `VCardEntry.AddProblem` is the one mutation, as `SpreadsheetRow<T>.AddError`: the handler adds its own problems
  before its preview or `ToAppError`. It does not remove the contact; a handler that wants to skip a contact decides
  that itself.
- The problem codes live in the package, not in `ErrorCodes`, for the reason of plan 0003.
- `ToAppError`'s default message: `Contact {index} ({displayName}), line {line}: {message}`, or without the name when
  the entry has no contact, or `{message}` for a file error.
- The equality of the model is structural (lists compared by element, `ReadOnlyMemory<byte>` by content), so a test
  and an application can compare a contact read back with the one written.

## Reading pipeline

All internal, in `Elarion.VCards/Reading`. One pass over the input, no recursion, no regular expressions on
unbounded input.

1. **Buffer** (FR-046): read the stream into a pooled buffer, refusing with `vcard.too_large` as soon as
   `MaxBytes + 1` bytes arrived. Cancellation is checked per chunk.
2. **File encoding** (FR-012): a UTF-8 BOM is skipped; a UTF-16 LE or BE BOM, or `B\0E\0` / `\0B\0E` before the first
   line break, transcodes the buffer to UTF-8 once (bounded: UTF-16 to UTF-8 at most grows by half) and marks every
   value as "already decoded". Otherwise the buffer stays bytes.
3. **Content lines** (`ContentLineReader`, FR-004–FR-011): an iterator over the buffer that yields logical lines as
   spans plus the physical line number of their first line. Line ends CR, LF, CRLF and `\r\r\n` all end one line.
   Unfolding appends continuation segments into one growing pooled buffer per logical line (linear, FR-051) and stops
   appending past `MaxLineBytes` (FR-049) while still consuming the continuation, except for a photo, where the limit is
   `MaxPhotoBytes × 4 / 3` plus folding slack (FR-050). Quoted-printable soft breaks join the next physical line
   whatever it starts with (FR-010). A 2.1 Base64 value without folding whitespace collects lines until a blank line or
   a line that parses as `name[;params]:` (FR-011). Blank lines and `#` lines between properties are skipped (FR-005).
   The tokenizer splits group, name, parameters (quoted values, bare 2.1 parameters, comma lists, spaces after `;`,
   RFC 6868 carets, FR-007, FR-008) and the raw value bytes.
4. **Value decoding** (`ValueDecoder`, FR-013–FR-017): `ENCODING=QUOTED-PRINTABLE` → bytes (an invalid escape keeps its
   characters); `ENCODING=b|BASE64` → bytes for binary properties. Text bytes → string by the ladder: known `CHARSET` →
   that encoding; else strict UTF-8 (`UTF8Encoding(false, throwOnInvalidBytes: true)` via `Utf8.IsValid` first, no
   exception on the hot path); else the fallback. Encodings come from a package-owned cache: built-in ones from
   `Encoding.GetEncoding`, others from `CodePagesEncodingProvider.Instance.GetEncoding(name)`, never
   `Encoding.RegisterProvider` (FR-016). A fallback name that resolves to nothing throws `InvalidOperationException` at
   registration or at the call (misconfiguration). Then backslash unescaping (FR-009) and line-break normalization to
   `\n` (FR-017).
5. **Cards** (FR-019, FR-035–FR-037, FR-047, FR-048): a state machine over the lines: `BEGIN:VCARD` opens an entry
   (closing an unterminated one with `vcard.missing_end`), `END:VCARD` closes it, a line without `:` is
   `vcard.malformed_line`, lines outside a card are ignored. A card counts its properties and, past the limit, keeps
   consuming to its `END` and ends without a contact. The entry limit stops the read.
6. **Mapping** (`ContactMapper`, FR-020–FR-034): properties of one card are collected first (by group, for
   `X-ABLabel`, `X-ABDATE` and Apple's labels), then mapped. The version comes from `VERSION` (default 3.0 rules when
   absent; 2.1 rules enable bare parameters and Base64 to a blank line). Type mapping from one table: `TYPE` values,
   bare 2.1 parameters, `PREF`/`pref`/`PREF=1`, Apple labels (FR-021, FR-022); unknown `TYPE` values (`INTERNET`,
   `X-…`, `waid=`) are ignored for the flags and kept in the unmapped form only when the whole property is unmapped.
   Dates by one small parser (FR-023); 1604 → no year. Phone numbers lose directional marks (FR-018). `ORG` splits on
   unescaped `;` (FR-020), `N` and `ADR` likewise, components with unescaped `,` joined by `, `. A value that fails its
   parser becomes `vcard.invalid_value` with the kind (`date`, `photo`, `uri`) and the property is dropped (FR-034).
   `AGENT` and everything else unmapped become `VCardProperty` with the decoded, still-escaped value text (FR-033), so
   the writer can write them back unchanged (FR-060).
7. **Photos** (`PhotoReader`, FR-028–FR-032, FR-050): Base64 decoded into a buffer bounded by `MaxPhotoBytes` with
   `Base64.DecodeFromUtf8` in chunks (whitespace inside is skipped); a `data:` URI parsed by hand (media type, optional
   `;base64`); the bytes sniffed by magic number (`FF D8 FF`, `89 50 4E 47`, `GIF8`, `RIFF….WEBP`, `BM`). A URI value
   (4.0 `PHOTO:https://…`, 3.0 `VALUE=uri`, or a value that parses as an absolute `http`/`https` URI, as Google writes
   it) becomes `Uri`; nothing is requested (FR-031).
8. **Result**: entries in file order; problems carry the entry's display name, computed once the card is mapped.

## Writer

`Elarion.VCards/Writing`, forward-only into a pooled UTF-8 buffer flushed with `WriteAsync` per contact (FR-062).

- Property order per card: `BEGIN`, `VERSION`, `KIND`/`X-ABSHOWAS`, `FN`, `N`, `NICKNAME`, `ORG`, `TITLE`, `ROLE`,
  `TEL`, `EMAIL`, `ADR`, `URL`, `BDAY`, `ANNIVERSARY` (4.0) or `X-ANNIVERSARY` and `item.X-ABDATE` (3.0), `NOTE`,
  `CATEGORIES`, `PHOTO`, `UID`, unmapped properties, `END` (FR-064).
- Labels: values with a custom label get a group `itemN` (N counting per card) and an `itemN.X-ABLabel` line
  (FR-055). 3.0 types as `TYPE=CELL,VOICE`-style lists in upper case, `TYPE=pref` for preferred; 4.0 types in lower
  case and `PREF=1`.
- Escaping per RFC 6350 §3.4 and RFC 2426 (FR-054); parameters with `:`, `;` or `,` are quoted; RFC 6868 carets in 4.0
  parameters.
- Folding (FR-053): count octets, break before 75, never inside a UTF-8 sequence, continuation starts with one space.
- Dates (FR-057), kinds (FR-058), `FN`/`N` (FR-059) as ADR-0110 decision 8 says. Photos (FR-056): 3.0
  `PHOTO;ENCODING=b;TYPE=JPEG:` with the sniffed subtype, 4.0 `PHOTO:data:image/jpeg;base64,…`, URI photos as
  `PHOTO;VALUE=uri:` (3.0) or `PHOTO:` (4.0).
- Unmapped properties are validated (name and parameter names `[A-Za-z0-9-]+`, a value without CR or LF) and written as
  stored (FR-060, FR-061).
- `ToFileAsync` writes to a `MemoryStream`; `ToStreamedFile` wraps `ElarionStreamedFile.FromWriter` (FR-063).

## Tests

All in `tests/Elarion.Tests/VCards/`, named after the acceptance scenarios.

- **Corpus** under `VCards/Fixtures/`: real exports from the producers of the research note, taken from the ez-vcard,
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
- **`ToAppError`** and **application problems** (FR-044, FR-045).
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
  (`FileExtensions = [".vcf", ".vcard"]`), mapping to an entity, a preview with problems, export as a file and as a
  streamed file, one file per contact for Outlook classic, the photo-URL caution.
- `docs/reference/packages.mdx`, `configuration.mdx` (`VCardOptions`), the error-code listing if one exists for
  package codes; `CHANGELOG.md`; the glossary term *vCard contact*.
- The file-format rule in `.github/instructions/framework.instructions.md` (added by PR #216) gains one sentence:
  a format whose schema a standard fixes may use a fixed model instead of `[<Area>Record]`, as ADR-0110 records.
  Done in task 1 if PR #216 has merged, else in the pull request that follows it.

## Tasks

1. **Project, model, options, registration, fixtures** — FR-001, FR-002, FR-020, FR-021, FR-027; collect and license-check
   the corpus.
2. **Content-line reader and decoding** — FR-003–FR-017, FR-046, FR-049, FR-051.
3. **Cards, mapping and the result** — FR-018–FR-026, FR-033–FR-045, FR-047, FR-048.
4. **Photos** — FR-028–FR-032, FR-050.
5. **Writer and file tiers** — FR-052–FR-065.
6. **Hostile-input suite, AOT sample, CI publish, benchmarks** — FR-066, SC-003, SC-005.
7. **Documentation** — capability page, references, changelog, the file-format rule sentence.

Task 2 comes first; 3 and 5 are independent after 2 (5 needs only the model of 1); 4 follows 3; 6 and 7 close.

## Shared with iCalendar

The iCalendar design session (ADR-0109, spec 0004) needs the same content-line reader and the same per-value decoding.
The reader is written without vCard knowledge (no property names, no version rules inside it: those are passed in as a
small rule set). Whichever package ships first owns it as internal code; when the second ships, it moves to a shared
internal location chosen then (a source-shared folder compiled into both, or a small internal package), without a
public API change.

## Risks

- **The corpus may hide quirks no fixture shows** (Outlook's non-ASCII output, UTF-16 files, a missing `END`): the
  research found no primary evidence for them. Mitigation: the per-value ladder and the defensive rules, and the
  consuming application's own files as a check before release, anonymized.
- **Sniffing may disagree with what a producer meant** (a HEIC photo from a newer iPhone): it is dropped with a problem
  today; adding a format is a table entry.
- **Equality of the model** must stay structural as members are added; a test enumerates the members to catch a
  forgotten one.
