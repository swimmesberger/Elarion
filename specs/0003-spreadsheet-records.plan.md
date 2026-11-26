---
type: Plan
title: "Plan 0003: spreadsheet records"
description: "How Elarion.Spreadsheets of spec 0003 is built: projects, public surface, generator, writer, reader, validation, tests and tasks."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-24T17:30:00Z }
status: draft
---
# Plan 0003: spreadsheet records

How [spec 0003](0003-spreadsheet-records.md) is built. The design and its rejected alternatives are in
[ADR-0108](../docs/decisions/0108-spreadsheet-records.md); this plan holds the implementation decisions behind it. Names
below are the intended ones and may move during review; the FR numbers are fixed.

## Projects

| Project | Target | Holds |
| --- | --- | --- |
| `src/Elarion.Spreadsheets` | `net10.0;net11.0`, `IsAotCompatible` | Attributes, public API, writer, reader, options, DI registration |
| `src/Elarion.Spreadsheets.Generators` | `netstandard2.0` | `SpreadsheetRecordGenerator` and `ELSHEET001`–`ELSHEET007`; bundled into the package's `analyzers/dotnet/cs/` the way `Elarion.Sql` bundles its generator |
| `src/Elarion.Abstractions` (changed) | | `ValidationRootAttribute` marker; `IRequestValidator.HasMetadata(Type)` (FR-048) |
| `src/Elarion.Validation` (changed) | | Implements `HasMetadata` from the registered resolvers |
| `src/Elarion.Generators` (changed) | `netstandard2.0` | `ValidationResolverGenerator` takes `[ValidationRoot]` types as roots; `ELVAL004`; `ELVAL002` extended (FR-061, FR-065) |

`Elarion.Spreadsheets` references `Elarion.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`,
`Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder` (with the configuration-binding generator, as
`Elarion.Typst` does) and the reader library (Sylvan.Data.Excel unless task 1 decides otherwise), pinned in
`Directory.Packages.props`. No reference to `Elarion.Blobs`, ASP.NET Core or EF Core (FR-001).
`DocumentFormat.OpenXml` is added to `tests/Elarion.Tests` only.

## Public surface

```csharp
namespace Elarion.Spreadsheets;

// Attributes (FR-002–FR-010)
[SpreadsheetRecord(string? worksheetName = null)]                 // classes only (FR-062)
[SpreadsheetColumn(string? header = null)] { Order, Format, Width, Aliases, TrueLabel, FalseLabel }
[SpreadsheetIgnore]

// Generated per record class T (partial), mirroring ISqlRecord<TSelf>/ISqlRowMapper<T>:
public interface ISpreadsheetRecord<TSelf> where TSelf : class, ISpreadsheetRecord<TSelf> {
    static abstract ISpreadsheetRowMapper<TSelf> SpreadsheetMapper { get; }
}
//   T.Columns.Email : SpreadsheetColumn<T>  — one constant per column (FR-035); members named Columns or
//   SpreadsheetMapper on T are ELSHEET007 (FR-070). The generated half also carries [ValidationRoot] when T has
//   validation attributes. A sealed {T}SpreadsheetRowMapper holds the column table and the row code.

// Writing (FR-012–FR-027, FR-063, FR-064): one type
public sealed class SpreadsheetWorkbook : IAsyncDisposable {
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static SpreadsheetWorkbook Create(Stream destination, SpreadsheetWriteOptions? options = null);
    public ValueTask AddWorksheetAsync<T>(IEnumerable<T> records, SpreadsheetWorksheetOptions? options = null, CancellationToken ct = default)
        where T : class, ISpreadsheetRecord<T>;
    public ValueTask AddWorksheetAsync<T>(IAsyncEnumerable<T> records, SpreadsheetWorksheetOptions? options = null, CancellationToken ct = default)
        where T : class, ISpreadsheetRecord<T>;

    // One-worksheet shortcuts
    public static ValueTask WriteAsync<T>(Stream destination, IEnumerable<T> | IAsyncEnumerable<T> records,
        SpreadsheetWriteOptions? options = null, CancellationToken ct = default) where T : class, ISpreadsheetRecord<T>;
    public static ValueTask<ElarionFile> ToFileAsync<T>(IEnumerable<T> | IAsyncEnumerable<T> records, string fileName,
        SpreadsheetWriteOptions? options = null, CancellationToken ct = default) where T : class, ISpreadsheetRecord<T>;
    public static ElarionStreamedFile ToStreamedFile<T>(IAsyncEnumerable<T> records, string fileName,
        SpreadsheetWriteOptions? options = null) where T : class, ISpreadsheetRecord<T>;
    // Several worksheets, streamed
    public static ElarionStreamedFile ToStreamedFile(string fileName,
        Func<SpreadsheetWorkbook, CancellationToken, ValueTask> write, SpreadsheetWriteOptions? options = null);
}
public sealed record SpreadsheetWriteOptions { int DropdownRows = 2000; int WidthSampleRows = 200; int MaxColumnWidth = 80; }
public sealed record SpreadsheetWorksheetOptions { string? Name; }

// Reading: a sealed service, because rows are validated through IRequestValidator (FR-047)
services.AddElarionSpreadsheets(configuration?);   // binds SpreadsheetOptions { SpreadsheetReadOptions ReadDefaults }

public sealed class SpreadsheetReader {
    public ValueTask<SpreadsheetReadDocument> OpenAsync(Stream workbook, SpreadsheetReadOptions? options = null, CancellationToken ct = default);
    // Shortcuts: open, read one worksheet, dispose
    public ValueTask<SpreadsheetReadResult<T>> ReadAsync<T>(Stream workbook, SpreadsheetReadOptions? options = null, CancellationToken ct = default)
        where T : class, ISpreadsheetRecord<T>;
    public ValueTask<SpreadsheetRowsResult> ReadRowsAsync(Stream workbook, SpreadsheetReadOptions? options = null, CancellationToken ct = default);
}
public sealed class SpreadsheetReadDocument : IDisposable {          // FR-069: parsed once
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }       // unreadable, too large (FR-053, FR-055)
    public IReadOnlyList<string> VisibleWorksheets { get; }
    public ValueTask<SpreadsheetReadResult<T>> ReadAsync<T>(SpreadsheetWorksheetSelection? selection = null, CancellationToken ct = default)
        where T : class, ISpreadsheetRecord<T>;
    public SpreadsheetRowsResult ReadRows(SpreadsheetWorksheetSelection? selection = null);
}
public sealed record SpreadsheetReadOptions {                        // one type; SpreadsheetOptions.ReadDefaults holds the defaults
    long MaxUnpackedBytes = 256 MiB; int MaxRows = 100_000; int MaxColumns = 256;
    IReadOnlyList<string> DateFormats; IReadOnlyList<string> TrueWords; IReadOnlyList<string> FalseWords;
}
public sealed record SpreadsheetWorksheetSelection { string? Name; int? HeaderRow; }

// Results: sealed classes with public constructors, so a handler's tests can build them
public sealed class SpreadsheetReadResult<T> where T : class {
    public string? Worksheet { get; }
    public IReadOnlyList<SpreadsheetRow<T>> Rows { get; }
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }
    public IEnumerable<SpreadsheetError> Errors { get; }             // file errors, then every row's, in order
    public IEnumerable<T> Records { get; }                           // rows without errors
    public bool HasErrors { get; }
    public bool Contains(SpreadsheetColumn<T> column);               // FR-035
    public AppError ToAppError(string? fieldPath = null,
        Func<SpreadsheetError, string>? format = null, int maxMessages = 100);   // FR-054
}
public sealed class SpreadsheetRow<T> where T : class {
    public int RowNumber { get; }
    public T? Record { get; }
    [MemberNotNullWhen(true, nameof(Record))] public bool IsValid { get; }
    public IReadOnlyList<SpreadsheetError> Errors { get; }
    public string CellText(SpreadsheetColumn<T> column);              // "" for a column the file lacks (FR-037)
    public void AddError(SpreadsheetColumn<T>? column, string code, string message);   // FR-068: located like ours; clears IsValid
}
public sealed class SpreadsheetError {
    public string Code { get; } public string Message { get; }
    public string? Worksheet { get; } public int? RowNumber { get; } public string? Header { get; } public string? CellReference { get; }
    public SpreadsheetValueKind? ExpectedKind { get; } public IReadOnlyList<string>? AllowedValues { get; } public long? Limit { get; }
}
public static class SpreadsheetErrorCodes { Unreadable = "spreadsheet.unreadable", TooLarge = "spreadsheet.too_large", ... }  // FR-066

public sealed class SpreadsheetRowsResult {                          // same file-error shape as records (FR-058)
    public string? Worksheet { get; }
    public IReadOnlyList<SpreadsheetCellRow> Rows { get; }
    public IReadOnlyList<SpreadsheetError> FileErrors { get; }
}
public sealed class SpreadsheetCellRow { public int RowNumber { get; } public IReadOnlyList<SpreadsheetCell> Cells { get; } }
public readonly struct SpreadsheetCell {                             // kind + one double + one string
    public SpreadsheetCellKind Kind { get; }
    public string Text { get; }                                      // FR-037 text, computed on access for numbers
    public bool TryGetNumber(out decimal value); public bool TryGetDouble(out double value);
    public bool TryGetBoolean(out bool value); public bool TryGetDate(out DateTime value);
}
```

- `SpreadsheetRow<T>.AddError` is the one mutation: the result is built by the reader and handed to the handler, which
  adds its own rule errors before building its preview or `ToAppError`. A row is never shared across threads.
- `SpreadsheetErrorCodes` lives in the package, not in `Elarion.Abstractions.Errors.ErrorCodes`: the codes belong to a
  read result, not to an `AppError` a handler returns.
- `ToAppError` returns `AppError.Validation` with field errors keyed by `fieldPath` (or `""`): up to `maxMessages`
  messages from `format` (default: `Row {n}, {header} ({cell}): {message}`), then one "and N more" message.

## Generator: `SpreadsheetRecordGenerator`

Incremental, `ForAttributeWithMetadataName("Elarion.Spreadsheets.SpreadsheetRecordAttribute")`, following
[ADR-0006](../docs/decisions/0006-incremental-source-generator-conventions.md): an equatable model per record, then emit.

- **Columns** (FR-003, FR-004): public instance properties with a getter, declaration order, then `Order`. Header from the
  attribute, else `[Display(Name)]` read as a constant, else the name.
- **Kinds** (FR-006): string, bool, the integral types, float, double, decimal, `DateTime`, `DateTimeOffset`,
  `DateOnly`, `TimeOnly`, `Guid`, enums, and `Nullable<>` of the value types; nullable annotations decide a string's
  requiredness.
- **Diagnostics**, in `AnalyzerReleases.Unshipped.md`: `ELSHEET001` unsupported property type, `ELSHEET002` not
  `partial`, `ELSHEET003` duplicate header or alias, `ELSHEET004` invalid worksheet name, `ELSHEET005` boolean labels
  misused (FR-060), `ELSHEET006` a struct (FR-062), `ELSHEET007` a member named like a generated one (FR-070).
- **Emitted per record**: the self-mapping half (`ISpreadsheetRecord<T>`, `Columns`, `[ValidationRoot]` when the type
  graph carries DataAnnotations or `IValidatableObject`) and `{T}SpreadsheetRowMapper` with a static column table, typed
  write and read code per column, and enum label switches in both directions (FR-009). Reading constructs the record
  with an object initializer; a property without a setter or `init` is write-only (FR-042).
- Generated code calls only `Elarion.Spreadsheets.Generated` members (`[EditorBrowsable(Never)]`), as the SQL mappers
  do. No reflection (FR-011).

## Writer

Own code under `Elarion.Spreadsheets/Writing`, on `ZipArchive.CreateAsync` (.NET 10).

- **Parts**: `[Content_Types].xml`, `_rels/.rels`, `xl/workbook.xml`, `xl/_rels/workbook.xml.rels`, `xl/styles.xml`, one
  worksheet part per worksheet, and a hidden `lists` worksheet when a dropdown list exceeds 255 characters. Worksheets
  are written as they come; workbook, rels, styles and content types at dispose, when every worksheet and format is
  known (zip entry order is free).
- **XML** as UTF-8 bytes into a pooled buffer flushed with `WriteAsync` (FR-023). Text cells are inline strings, so no
  shared-strings table is held. Characters XML cannot carry, and literal `_xHHHH_` sequences, use the `_xHHHH_` escape
  (FR-016). No cell gets an `<f>` element (FR-015).
- **Styles** (FR-017, FR-018, FR-020, FR-063): a fixed `cellXfs` table — default, header (bold, fill `#EEF2F7`, thin
  bottom border), built-in 14, 22 and 21, text (built-in 49, `@`) — plus one entry per distinct `Format`. Text columns
  get the text style on written rows and, through a `<cols>` default style, on empty rows. The header row is frozen.
- **Widths** (FR-019): read up to `WidthSampleRows` records ahead, measure, write `<cols>`, write the buffered records,
  stream the rest.
- **Dropdowns** (FR-021, FR-022): `<dataValidations>` after `<sheetData>` over rows 2 to `max(lastRow, DropdownRows + 1)`.
- **Values** (FR-013, FR-014, FR-026, FR-064): numbers in invariant round-trip notation; `NaN` and infinities refused;
  dates as serials with the 1900 leap-year correction, dates before 1900-01-01 refused; `DateTime` written as its
  wall-clock value whatever its `Kind`; `DateTimeOffset` as ISO 8601 text with its offset.
- **Limits** (FR-026, FR-027): `SpreadsheetWriteException` naming row and column, or the worksheet.
- **Determinism** (FR-025): entry timestamps 1980-01-01, a fixed compression level, no `docProps`; asserted per runtime.
- **File tiers** (FR-024): `ToFileAsync` writes to a `MemoryStream`; `ToStreamedFile` wraps
  `ElarionStreamedFile.FromWriter`.

## Reader

`Elarion.Spreadsheets/Reading`, behind the internal `IWorkbookSource` (open from a buffer, list worksheets with their
visibility, iterate a worksheet's rows, typed cell access). `SylvanWorkbookSource` implements it: a source read of
Sylvan.Data.Excel 0.5.8 shows it already skips hidden worksheets (`ReadHiddenWorksheets` defaults to false) in all
three formats, detects dates by built-in and custom number formats (`GetFormat(i).Kind`), honours the 1904 system,
reads cached formula values without evaluating them, maps Strict OOXML, and exposes `RowNumber`. What it lacks is
hardening at the boundary, which the package owns in one pre-pass (below). The task-1 spike confirmed this; its
results are under [Reader spike result](#reader-spike-result).

- **Open once, behind one pre-pass** (FR-069): `OpenAsync` buffers the input (bounded by the upload limits) and detects
  the format from the magic bytes. `PK` → zip, and the pre-pass sums the uncompressed sizes the central directory
  declares and refuses the file above `MaxUnpackedBytes` (FR-055) — .NET's zip reader ends each entry at its declared
  size, so a lying size truncates the part (an `XmlException`, caught) instead of inflating past it —, scans
  `workbook.xml`, `styles.xml` and the `.rels` parts for `<!DOCTYPE` and refuses one (FR-057: the library loads them with
  `XmlDocument`, which parses DTDs; worksheet and shared-string parts already prohibit them), and reads each sheet's
  `state` from `xl/workbook.xml`, because the library treats `veryHidden` as visible (an upstream fix is offered as a
  small pull request). Then `.xlsb` when
  `xl/workbook.bin` exists, else `.xlsx`. `D0 CF 11 E0` → compound file: `EncryptedPackage` means password-protected
  (`spreadsheet.unreadable`), else `.xls`. The library gets the verified buffer through its synchronous
  `Create(Stream, type)`, which reads a seekable stream in place; `CreateAsync` would copy it again. Every exception the
  library throws on broken input (`InvalidDataException`, `XmlException`, `InvalidCastException`, …) is caught at the
  adapter and becomes `spreadsheet.unreadable` (FR-049, FR-053). `SpreadsheetReadDocument` keeps the opened source.
- **Code pages** (FR-028): the library's `.xls` reader needs Windows-1252 in a static initializer; without the
  code-page provider it fails with a `TypeInitializationException` that stays for the life of the process. The package
  registers `CodePagesEncodingProvider` (process-wide, idempotent) before its first read.
- **Worksheet choice** (FR-029, FR-067): first visible unless named; the library's hidden-sheet skipping plus the
  pre-pass's `veryHidden` list for `.xlsx`.
- **Header row and columns** (FR-030–FR-033, FR-005, FR-036): first non-empty row unless named; trim and invariant
  lower case; header, then alias unless the own header is present; `spreadsheet.column_duplicated`,
  `spreadsheet.column_missing`. A `ColumnMap` holds an ordinal per record column or −1.
- **Cells** (FR-037–FR-046): typed getters per kind; a `TimeOnly` comes from the fractional part of the number
  (`GetDouble`), because the library's `GetDateTime` of a time-only value depends on the date system and the file
  format (year 1, 1900 or 1904), and a value of a day or more is an error for it; the lenient rules in one internal `LenientText` class (grouping in
  threes, `d.M.yy` as 20yy, configured formats and words, ISO offsets). Number cells to `decimal` via `(decimal)double`
  (15 significant digits); number text without an exponent.
- **Every row** (FR-034, FR-052, FR-056): row number, cells, errors, record when error-free; no error cap; the row limit
  keeps the rows before it and does not count skipped empty rows.
- **XML safety** (FR-057): the pre-pass refuses any XML part with a DOCTYPE before the library parses it; the package's
  own XML reads use `DtdProcessing.Prohibit`.
- **Plain rows** (FR-058, FR-059): the same source without header resolution, plus the column limit.

## Validation

- `Elarion.Abstractions` gains `[ValidationRoot]`: a type that is validated outside a handler request. The spreadsheet
  generator puts it on the generated half of a record that carries validation attributes.
- `ValidationResolverGenerator` adds `[ValidationRoot]` types to the roots of the module whose namespace contains them,
  reports `ELVAL004` (error) for one outside every module (FR-061), and extends `ELVAL002` (warning) to roots in an
  assembly without `Elarion.Validation` (FR-065). The core generator learns a neutral marker, not the package.
- `IRequestValidator` gains `bool HasMetadata(Type type)`; the `Elarion.Validation` implementation answers from the
  registered resolvers, so a disabled module answers no.
- At read time, for a record whose mapper says it carries validation attributes: no `IRequestValidator`, or
  `HasMetadata(typeof(T))` false, throws `InvalidOperationException` naming the cause (FR-048). Each error-free record is
  validated with `ValidateAsync(typeof(T), record)`; wire-named paths map to columns through the mapper's wire-name
  table; a path that names no column becomes a row-level error (FR-047).

## Tests

All in `tests/Elarion.Tests`, named after the acceptance scenarios.

- `Spreadsheets/` — writer, reader, round trips, lenient rules, errors, application errors, `ToAppError`, plain rows,
  limits. Fixture workbooks saved by Excel, LibreOffice and Google Sheets (`.xlsx`, `.xls`, `.xlsb`, 1904 system, hidden
  first sheet, rich text, Strict OOXML, formula cells, text typed into a template) under `Spreadsheets/Fixtures/`;
  hostile fixtures (a zip whose declared sizes lie, DTD with external entity, truncated, encrypted) built in code.
- Every written file is validated with `OpenXmlValidator` and read back through the package's reader.
- `Generators/SpreadsheetRecordGeneratorTests.cs` — emitted shape and `ELSHEET001`–`ELSHEET007`;
  `ValidationResolverGenerator` tests for `[ValidationRoot]`, `ELVAL004` and `ELVAL002`, with existing snapshots
  byte-identical.
- AOT: the `EdgeTelemetry` sample (already `PublishAot`) gains a readings export and import; CI publishes it with
  NativeAOT and fails on any IL2xxx/IL3xxx warning (SC-003), following `.github/instructions/workflows.instructions.md`.
- `tests/Elarion.Benchmarks` — writing 1,000 and 100,000 records to a null stream (SC-005), reading 10,000 rows.
- SC-001, including whether built-in formats 21 and 22 follow the viewer's locale in LibreOffice and Google Sheets, is a
  manual check recorded in the pull request.

## Documentation

- `docs/capabilities/spreadsheets.mdx` (and `meta.json`); `docs/reference/packages.mdx`, `diagnostics.mdx`
  (`ELSHEET001`–`ELSHEET007`, `ELVAL004`, `ELVAL002`), `configuration.mdx` (`SpreadsheetOptions`); the validation
  concept page (`[ValidationRoot]`, `HasMetadata`); `CHANGELOG.md`.

## Tasks

1. **Reader spike** — confirm Sylvan.Data.Excel behind `IWorkbookSource` against the go/no-go criteria below.
2. **Package, attributes, generator** — FR-001–FR-011, FR-060, FR-062, FR-070.
3. **Writer core** — FR-012–FR-020, FR-023, FR-025–FR-027, FR-063, FR-064.
4. **Dropdowns and templates** — FR-021, FR-022.
5. **File tiers** — FR-024.
6. **Reader source, detection and limits** — FR-028–FR-031, FR-040, FR-053, FR-055–FR-057, FR-067, FR-069.
7. **Record reading and the result** — FR-032–FR-039, FR-041–FR-046, FR-049–FR-052, FR-054, FR-066, FR-068.
8. **Validation** — FR-047, FR-048, FR-061, FR-065: `[ValidationRoot]`, `HasMetadata`, the generator change.
9. **Plain rows** — FR-058, FR-059.
10. **AOT sample, CI publish, benchmarks** — SC-003, SC-005.
11. **Documentation** — capability page, references, changelog.

Tasks 2–5, 6–7 and 8 are independent streams after task 1 (8 does not need it); 9 follows 6; 10 and 11 close.

## Reader go/no-go (task 1)

Keep Sylvan.Data.Excel when all four hold:

1. The fixture corpus (Excel, LibreOffice, Google Sheets; `.xlsx`, `.xls`, `.xlsb`; 1904; custom date formats; hidden
   and `veryHidden` sheets; Strict OOXML; formulas) passes through the library's public API only — no fork, no
   reflection.
2. Every hostile fixture (lying zip sizes, a DOCTYPE or entity bomb in `workbook.xml`, `styles.xml` and the `.rels`
   parts, truncated files, encrypted `.xlsx` and `.xls`, garbage BIFF) ends as a caught error within the memory limit —
   no hang, no stack overflow.
3. The NativeAOT publish on net10.0 shows no IL2xxx/IL3xxx warning from the library's net8.0 asset.
4. The adapter and the pre-pass stay around 200 lines.

If a fix needs a fork, or `.xls` cannot be bounded from outside, switch to an own `.xlsx` reader behind the same
interface and amend FR-028 to drop `.xls` and `.xlsb` (with the ADR and the upload example), rather than keeping the
library for the binary formats only: two readers in parity cost more than either.

## Reader spike result

Run on 2026-11-26 against Sylvan.Data.Excel 0.5.8, .NET 10.0.404, osx-arm64, with a throwaway probe outside the
repository. **Go**, with the corpus still to complete in task 6.

| Criterion | Result |
| --- | --- |
| 1. Corpus through the public API | Passes for hand-written `.xlsx` (shared and rich-text strings, inline strings, cells without references, custom and locale date formats, `[h]:mm`, the text format, booleans, error cells, formulas with and without cached values, a `_x0001_` escape, missing rows), the same file saved by LibreOffice as `.xlsx` and `.xls`, the 1904 system in `.xlsx` and `.xls`, and Strict OOXML. Not yet covered: `.xlsb` (LibreOffice cannot write it), files saved by Excel and Google Sheets, password-protected files (LibreOffice's headless conversion ignored the password) |
| 2. Hostile files | All caught, none hung: truncated zip and garbage BIFF (`InvalidDataException`), an entity bomb in `workbook.xml` (`XmlException` after .NET's entity cap, ~170 MB allocated and 0.7 s — the DOCTYPE pre-scan avoids this), DTDs in `styles.xml` and `.rels` (accepted silently — the pre-scan refuses them), a DTD in a worksheet (prohibited by the library), a 400 MB part declared as 1,000 bytes (truncated at 1,000, `XmlException`), the same part declared honestly (streamed in 1.2 s with ~1 MB allocated; the size check refuses it first) |
| 3. NativeAOT | `dotnet publish -p:PublishAot=true -p:TrimmerRootAssembly=Sylvan.Data.Excel`: zero warnings with the whole library analyzed; the native binary reads the fixtures identically |
| 4. Adapter size | Not measured yet; the pre-pass is now a size sum, three DOCTYPE scans and one `state` read, well inside 200 lines |

Findings that shape the code beyond the pre-pass: the code-page registration and the `TimeOnly` rule above; a workbook
whose sheets are all hidden reports no worksheets (→ `spreadsheet.no_visible_worksheet`); a format with a color and two
sections (`[Red]dd/mm/yy;[Blue]0.00`) is not recognized as a date (accepted: such a cell reads as a number); a rich-text
run without `xml:space="preserve"` loses its trailing space, as the format allows; a cell with the text format `@`
reports its format kind as text even when it holds a number.

## Risks

- **The reader library may need more workarounds than it saves.** Today they are one pre-pass; task 1 decides with
  the criteria above, and the internal interface keeps the choice inside the package. Password-protected `.xlsx` and
  `.xls` (BIFF `FilePass`) are still untested; they join the corpus in task 6, saved from Excel.
- **The validator seam changes** (`HasMetadata`): every `IRequestValidator` implementation must answer it; the only
  one shipped is `Elarion.Validation`'s, and a default interface member answering `true` would fail open, so it is
  abstract.
- **`ValidationResolverGenerator` change touches every application's generated code.** It only adds roots for
  `[ValidationRoot]` types; existing snapshots must stay byte-identical.
