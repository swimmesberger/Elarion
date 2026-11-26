---
type: Research note
title: "vCard libraries and producers: what .NET offers under NativeAOT, and what real exports contain"
description: "A survey of .NET vCard libraries for NativeAOT, maintenance and hostile-input behavior, and of what Android, Google, Apple, Outlook, Thunderbird and Nextcloud write into .vcf files; input for ADR-0110."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T15:40:00Z }
status: draft
sources:
  - https://github.com/FolkerKinzel/VCards
  - https://github.com/bolorundurowb/vCardLib
  - https://github.com/mixerp/MixERP.Net.VCards
  - https://github.com/EWSoftware/PDI
  - https://github.com/gachris/vCard.Net
  - https://github.com/Sicos1977/VCardReader
  - https://azuresearch-usnc.nuget.org/query?q=vcard&take=30
  - https://learn.microsoft.com/en-us/dotnet/api/system.text.codepagesencodingprovider?view=net-10.0
  - https://github.com/dotnet/runtime/blob/main/src/libraries/NetCoreAppLibrary.props
  - https://www.rfc-editor.org/rfc/rfc2425
  - https://www.rfc-editor.org/rfc/rfc2426
  - https://www.rfc-editor.org/rfc/rfc6350
  - https://www.rfc-editor.org/rfc/rfc6868
  - https://www.rfc-editor.org/rfc/rfc5545
  - https://web.archive.org/web/2015id_/http://www.imc.org/pdi/vcard-21.txt
  - https://android.googlesource.com/platform/frameworks/opt/vcard/+/refs/heads/main/java/com/android/vcard/VCardBuilder.java
  - https://android.googlesource.com/platform/frameworks/opt/vcard/+/refs/heads/main/java/com/android/vcard/VCardConfig.java
  - https://android.googlesource.com/platform/packages/apps/Contacts/+/refs/heads/main/src/com/android/contacts/vcard/ImportProcessor.java
  - https://android.googlesource.com/platform/frameworks/opt/vcard/+/refs/heads/main/tests/res/raw/
  - https://github.com/mangstadt/ez-vcard/tree/782cb85d2d186e689193f11b6849cbb657777576/src/test/resources/ezvcard/io/text/
  - https://developers.google.com/people/carddav
  - https://support.google.com/contacts/answer/15147365?hl=en
  - https://support.apple.com/guide/contacts/adrbk1512/mac
  - https://support.apple.com/guide/icloud/mmfba748b2/icloud
  - https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxvcard/
  - https://support.microsoft.com/en-us/outlook/import-contacts-from-icloud-vcard-vcf-file-to-outlook
  - https://support.microsoft.com/article/285a3b55-8d93-4ac8-93df-43fffd13b2f1
  - https://hg-edge.mozilla.org/comm-central/raw-file/tip/mailnews/addrbook/modules/VCardUtils.sys.mjs
  - https://hg-edge.mozilla.org/comm-central/raw-file/tip/mailnews/addrbook/modules/AddrBookUtils.sys.mjs
  - https://github.com/sabre-io/dav/blob/master/lib/CardDAV/VCFExportPlugin.php
  - https://github.com/sabre-io/vobject/blob/master/lib/VCardConverter.php
---
# vCard libraries and producers

- Date: 2026-11-26
- Status: Research input for [ADR-0110](../decisions/0110-vcard-contacts.md). This is not a decision.
- Questions: is there a maintained .NET vCard library that is safe under NativeAOT and against hostile uploads, and
  what do the applications people export contacts from actually write, so a reader is tolerant where it must be and a
  writer produces what they import?
- Method: each library's project file and source read on its default branch at a pinned commit; releases and
  dependencies from the NuGet registration API; activity from the GitHub API. Producer behavior from producer source
  code (AOSP, Thunderbird, sabre/dav, Nextcloud), vendor documentation (Apple, Google, Microsoft, MS-OXVCARD), and
  real exports kept as test fixtures by ez-vcard and AOSP. Nothing was compiled, published with NativeAOT or fuzzed
  for this note. **Inference** marks my reading; forum and vendor-blog evidence is called **secondary**.

## Summary

1. **No .NET vCard library declares AOT or trimming compatibility.** None of the surveyed projects sets
   `IsAotCompatible`, `IsTrimmable` or `EnableTrimAnalyzer`. FolkerKinzel.VCards is the only maintained library with
   broad coverage and no reflection found in its source; the others are unmaintained, Windows-bound or use
   reflection that NativeAOT warns about.
2. **FolkerKinzel.VCards is not built for hostile input.** It has no size, line, card or nesting limit, treats an
   `OutOfMemoryException` while reading a line as the end of the file, drops malformed lines and invalid Base64
   silently, reports no line numbers, and parses an embedded `AGENT` vCard recursively without a depth limit. Using it
   also registers the code-page provider process-wide and sets a static application id.
3. **Producers write all three versions, often in one file.** Android writes 2.1 with quoted-printable UTF-8, Apple
   and Google write 3.0, Thunderbird and Nextcloud write 4.0 for new cards and keep the stored version of synced
   ones, so one export can mix 3.0 and 4.0. Outlook classic writes 2.1, one contact per file.
4. **vCard 3.0 is the safest version to write.** It is Apple's default and Google's native format, the AOSP importer
   accepts 2.1 and 3.0 but not 4.0, and Microsoft's Exchange conversion references RFC 2426 only. Outlook classic
   imports only the first contact of a multi-contact file.
5. **vCard and iCalendar share one content-line grammar** (folding, `NAME;PARAM=value:value`, backslash escaping,
   RFC 6868 caret escaping). A reader for one is most of a reader for the other.

## Libraries

| Library | Latest release | Targets | AOT flags | Activity (2026-11-26) |
| --- | --- | --- | --- | --- |
| FolkerKinzel.VCards | 8.2.0, 2026-07-12 | net10.0, net9.0, net8.0, netstandard2.0/2.1, net462 | none | 29 stars, one maintainer, MIT; depends on four packages of the same author (DataUrls, MimeTypes, Strings, Helpers) |
| vCardLib.dll | 6.3.2, 2026-11-18 | netstandard1.3, netstandard2.0 | none; `Enum.GetValues(Type)` is `[RequiresDynamicCode]` | 33 stars, active, MIT |
| EWSoftware.PDI.Data | 2025.1.9, 2025-01-09 | net40, netstandard2.0 | none; non-generic `Enum.Parse`, `TypeDescriptor` binding | MS-PL |
| vCard.Net | 2.1.0, 2024-11-15 | netstandard2.0/2.1 | none; `Activator.CreateInstance(Type)` | 2 stars, MIT |
| MixERP.Net.VCards | 1.0.7, 2017-02-20 | net40–net461, netstandard1.x | n/a | unmaintained, no charset or quoted-printable decoding found |
| Thought.vCards | 1.0.9, 2017-04-12 | net40 | n/a | unmaintained |
| VCardReader | 1.2.3, 2023-02-11 | netstandard2.0 | n/a | depends on System.Drawing.Common, Windows-only since .NET 6 |

MimeKit maps `.vcf` to `text/vcard` and has no vCard parser. The .NET base library has no vCard type.

Sources: the NuGet search and registration API
(`https://api.nuget.org/v3/registration5-gz-semver2/<id>/index.json`), each repository listed in the frontmatter, and
a search for `IsAotCompatible|IsTrimmable|EnableTrimAnalyzer|EnableAotAnalyzer` over all of them, which found none.

### FolkerKinzel.VCards in detail

Read at commit `345111f8a8cbfac509896c438900098ee0376538`.

- **API.** A static `Vcf` class (`Load`, `Parse`, `Deserialize(Stream)`, `Serialize`, `AsString`), a mutable `VCard`
  model and a fluent `VCardBuilder`. `Vcf.Deserialize` materializes every card into an array; `DeserializeAsync`
  awaits the stream and then parses synchronously. `VcfReader(TextReader).ReadToEnd()` yields one card at a time but
  cannot be combined with the encoding detection below (`Vcf_Deserialization.cs`, `VcfReader.cs`).
- **Encoding detection (`AnsiFilter`).** Parses the whole input as UTF-8 with a decoder that records invalid bytes;
  when one is found, takes the first `CHARSET` of a name, address or text property of a 2.1 card, else a fallback code
  page (1252 by default), and **parses the whole input a second time** with it. One encoding per stream; no
  per-property charset, no UTF-16 detection without a byte-order mark (`AnsiFilter.cs`).
- **Global side effects.** FolkerKinzel.Strings calls `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
  whenever it builds an encoding (`TextEncodingConverter.cs`); `VCard.RegisterApp` sets a static that is set
  automatically on first use, after which a host's own call with another id throws (`VCard_Synchronization.cs`).
  Quoted-printable text is decoded with `Environment.NewLine`, so decoded notes differ between Windows and Linux
  (`QuotedPrintable.cs`).
- **Tolerance.** Unfolds folded lines, joins quoted-printable soft breaks including Outlook's padded ones, ends 2.1
  Base64 at a blank line or at the next line holding a `:`, keeps groups, `X-ABLabel` and unknown properties, decodes
  RFC 6868 caret escapes (`VcfRow*.cs`, `ParameterSection_ctor.cs`). Unknown properties are written only with a
  non-default option. Output is UTF-8 without a byte-order mark.
- **Hostile input.** No limit on file size, line length, property count or nesting (no limit in the deserializers).
  `ReadNextLine` catches `OutOfMemoryException` from `TextReader.ReadLine` and treats it as the end of the file
  (`VcfRowReader.cs#L38`, `#L57-L58`). A row without `:` is dropped silently, invalid Base64 becomes an empty array
  (`VcfRow_Method.cs`, `Base64Helper.cs`). A 3.0/4.0 `AGENT` value holding a vCard is parsed by a recursive call
  without a depth counter (`VCard_ctor.cs#L543-L555`). Repeated properties are chained with `Concat`
  (`VCard_ctor.cs#L566`); **Inference:** enumerating many of one kind is quadratic. SECURITY.md lists only 7.x as
  supported while 8.x is current.
- **AOT.** No reflection found in it or its dependency chain except a manifest-resource read in MimeTypes.
  **Inference:** a NativeAOT publish would likely be clean, but the project does not run the analyzers, so that is
  unproven.

### vCardLib in detail

Its one good idea is decoding each line with that line's `CHARSET`. But it copies the whole stream into memory twice,
an unknown charset name throws and aborts the parse, unfolding concatenates strings in a loop (quadratic for a folded
photo), properties are found by culture-sensitive `StartsWith` without a delimiter check (so `N` matches `NOTE`, and
`item1.TEL` is no phone), and malformed input throws a bare `Exception` (`vCardDeserializer.cs`,
`FileDataHelpers.cs`, `PropertyEncoding.cs`).

### Windows-1252 on .NET 10

.NET has built-in encodings only for ASCII, ISO-8859-1, UTF-7, UTF-8, UTF-16 and UTF-32; Windows-1252 needs
`CodePagesEncodingProvider`. The documentation recommends `Encoding.RegisterProvider`, which also changes
`Encoding.GetEncoding(0)` on Windows to the system ANSI code page, a process-wide effect. The provider ships in the
shared framework (listed in `NetCoreAppLibrary.props`), its data is an embedded resource, and the runtime's libraries
are built with `IsAotCompatible`. **Inference:** a package can hold its own instance from
`CodePagesEncodingProvider.Instance.GetEncoding(1252)` without registering it globally, and stays AOT- and
trim-safe.

## Producers

| Producer | Version written | Contacts per file | Text | Photos | Birthday without a year |
| --- | --- | --- | --- | --- | --- |
| Android (AOSP, Samsung) | 2.1 (default) | all in one | UTF-8; every non-ASCII value `CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE`, every byte encoded, soft break every 67 characters without leading whitespace, splitting UTF-8 sequences | `PHOTO;ENCODING=BASE64;JPEG:` (2.1), `PHOTO;ENCODING=B;TYPE=JPEG:` (3.0), the ~96 px thumbnail | whatever the account stored, often `--MM-DD` |
| Google Contacts | 3.0 | all in one (import: at most 3,000) | UTF-8; escapes `:` needlessly (`http\://`); invisible U+202A in phone numbers | a URL (`PHOTO:https://lh3.googleusercontent.com/…`), no `VALUE=uri` | `--1212` even in 3.0 |
| Apple (macOS, iOS, iCloud) | 3.0 (2.1 selectable on macOS) | all selected in one | UTF-8; lower-case repeated `type=`; `itemN.X-ABLabel:_$!<Mobile>!$_` labels; iOS 6 wrote `\r\r\n` line ends | `PHOTO;ENCODING=b;TYPE=JPEG:`; iOS 15 omits `TYPE` and writes PNG | `BDAY;X-APPLE-OMIT-YEAR=1604:1604-04-15`, iCloud also plain year 1604 |
| Outlook classic (Windows) | 2.1 | one | ASCII bare, multi-line values quoted-printable; bare parameters (`TEL;WORK;VOICE`); `X-MS-*`; Outlook 2003 split an escape across a soft break | `PHOTO;TYPE=JPEG;ENCODING=BASE64:`, blank-line end, plus an empty `X-MS-CARDPICTURE` | `X-MS-ANNIVERSARY` for anniversaries |
| Exchange (MS-OXVCARD) | 3.0 | one | `BEGIN:vCard` in mixed case | imports only `ENCODING=b`, bmp/gif/jpeg/png | — |
| Thunderbird 102+ | 4.0 for new cards, 3.0/4.0 as synced | all in one | UTF-8, folded at 75 | `data:` URI | year 1604 read as no year |
| Nextcloud / sabre/dav | as stored, mixed 3.0/4.0 | all in one, served as `text/directory` | UTF-8, folded at 75 | as stored | `--0124` in 4.0, `X-APPLE-OMIT-YEAR` in 3.0 |

New Outlook and Outlook.com document CSV as their only contact import and export format. Outlook classic imports only
the first contact of a multi-contact `.vcf` (Microsoft Q&A answer by a community MVP, and Microsoft's own advice to
convert a multi-contact iCloud file to CSV first; **secondary** for the first, primary for the second). Whether it
imports 4.0 has no primary source either way.

Sources per row: AOSP `VCardBuilder.java`, `VCardConfig.java`, `ImportProcessor.java` and its test fixtures; Google's
CardDAV page and contacts import help; Apple's Contacts and iCloud guides; Microsoft's Outlook support pages and
MS-OXVCARD; Thunderbird's `VCardUtils.sys.mjs` and `AddrBookUtils.sys.mjs`; sabre's `VCFExportPlugin.php` and
`VCardConverter.php`; the ez-vcard fixtures `John_Doe_ANDROID.vcf`, `John_Doe_IPHONE.vcf`,
`John_Doe_MAC_ADDRESS_BOOK.vcf`, `John_Doe_MS_OUTLOOK.vcf`, `outlook-2003.vcf`, `outlook-2007.vcf`, `gmail-single2.vcf`
and `gmail-list.vcf`.

## What a tolerant reader meets

| Quirk | Seen in |
| --- | --- |
| Versions 2.1, 3.0 and 4.0, and mixed versions in one file | all; Thunderbird and Nextcloud exports mix |
| Bare 2.1 parameters (`TEL;WORK;VOICE`, `PHOTO;ENCODING=BASE64;JPEG`), repeated or comma-joined `TYPE=`, lower-case `type=pref`, `PREF=1` | Android, Outlook, Apple, RFC 6350 |
| Quoted-printable soft breaks without leading whitespace, escapes and UTF-8 sequences split across them, padded `=` | Android, Outlook 2003 |
| `CHARSET=` per property: `UTF-8`, `us-ascii`, `Shift_JIS`, even in 3.0 for non-UTF-8 | Android, Outlook |
| Base64 ending at a blank line (2.1) or not (BlackBerry, 3.0); continuation indented by 1, 2 or 4 spaces, or not at all; empty Base64 values | Android, Apple, Outlook, AOSP fixtures |
| Line ends CRLF, LF, `\r\r\n`, mixed in one file | Google fixtures, iOS 6.1.3, old Mac Address Book |
| Needless escapes (`\:`), unescaped backslashes in 2.1, `\n` in notes and street lines | Google, Apple, Android |
| `BEGIN:vCard` in mixed case, all-lower-case property names (`version:2.1`), `X-ABLABEL` | Exchange, Thunderbird ≤ 78, Google |
| `#` comment lines and junk lines inside a card, a space after `;` in parameters | AOSP fixtures, Thunderbird bug 1659323 |
| No `N` and no `FN` (Android 2.1 without a name), empty `N:;;;;`, organization-only contacts (`X-ABSHOWAS:COMPANY`, `KIND:org`) | Android, Google, Apple |
| `ADR` with an eighth component holding the formatted address | Google |
| Dates `YYYYMMDD`, `YYYY-MM-DD`, `value=date`, date-times, `--MMDD`, year 1604 with or without `X-APPLE-OMIT-YEAR`, `VALUE=DATE,X-APPLE-OMIT-YEAR=1604` | all |
| Anniversaries as `ANNIVERSARY`, `X-ANNIVERSARY`, `X-MS-ANNIVERSARY`, `itemN.X-ABDATE` with an `_$!<Anniversary>!$_` label, `X-ANDROID-CUSTOM` | Outlook, Apple, Google, Android |
| Photos inline without `TYPE`, as a URL without `VALUE=uri`, as a `data:` URI | Apple, Google, Thunderbird |
| A byte-order mark, UTF-16, Windows-1252 without `CHARSET`, a missing `END:VCARD` | no primary evidence found. **Inference:** handle defensively; a consuming application already decodes uploads by UTF-16 byte-order mark, then UTF-8, then Windows-1252 |
| RFC 6868 caret escapes | not seen in any export; **Inference:** decode on read, they are rare |

## What a writer should produce

**Inference** from the table above: vCard 3.0, UTF-8 without a byte-order mark, CRLF line ends, folded at 75 octets
without splitting a UTF-8 sequence, `TYPE=` parameters, inline photos as `PHOTO;ENCODING=b;TYPE=JPEG`, no `CHARSET`
parameter, custom labels as `itemN.X-ABLabel`, and a birthday without a year as `BDAY;X-APPLE-OMIT-YEAR=1604:1604-MM-DD`,
which Apple, DAVx5, Nextcloud, sabre/vobject and Thunderbird read. 4.0 for importers that want it (Thunderbird,
Nextcloud). For Outlook classic, one contact per file.

## Standards in brief

- vCard 2.1 (versit, 1996): per-property `CHARSET` and `ENCODING` (`BASE64`, `QUOTED-PRINTABLE`, `8BIT`), bare
  parameters, Base64 ended by a blank line, several cards in one stream.
- RFC 2425 and RFC 2426 (3.0): `text/directory`, CRLF, folding at 75 by CRLF and one whitespace, `CHARSET` removed,
  `TYPE=` required, `ENCODING=b`; `FN`, `N` and `VERSION` are required.
- RFC 6350 (4.0): UTF-8 only, `text/vcard`, `.vcf` and `.vcard`; `text/x-vcard` and `text/directory` deprecated;
  folds at 75 octets keep multi-octet characters together, but readers should restore split ones; only `\,`, `\;`,
  `\\` and `\n` are escapes; truncated dates (`--0415`), `ANNIVERSARY`, `PREF=1..100`, `KIND` and `MEMBER`.
- RFC 6868: `^n`, `^^` and `^'` in parameter values.
- RFC 5545 (iCalendar) §3.1 uses the same content-line grammar as RFC 6350 §3.3.
