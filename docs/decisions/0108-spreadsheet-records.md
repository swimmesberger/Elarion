---
type: Architecture Decision Record
title: "ADR-0108: Spreadsheet records — a generated mapping in both directions, Elarion's own .xlsx writer, a library behind the reader"
description: "Elarion.Spreadsheets maps [SpreadsheetRecord] types to worksheet rows with generated code, writes .xlsx and fill-in templates itself, and reads workbooks through Sylvan.Data.Excel behind an internal interface into a result that keeps every row, states which columns the file has, and codes every problem."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-24T16:20:00Z }
status: stable
---
# ADR-0108: Spreadsheet records — a generated mapping in both directions, Elarion's own .xlsx writer, a library behind the reader

- Status: Accepted (not implemented)
- Date: 2026-11-24
- Related: [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node tier the defaults are sized for),
  [ADR-0027](0027-declarative-request-validation.md) (DataAnnotations through `IRequestValidator`),
  [ADR-0039](0039-binary-file-responses.md) (the small-file tier), [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md)
  (requiredness from nullability), [ADR-0093](0093-typst-pdf-rendering.md) (a framework package around an outside
  engine), [ADR-0098](0098-streamed-files-for-http-only-endpoints.md) (streamed files),
  [ADR-0105](0105-uploaded-files-are-handler-parameters.md) (uploaded files), the research note
  [XLSX libraries for NativeAOT](../research/2026-11-24-xlsx-libraries-for-native-aot.md), and
  [spec 0003](../../specs/0003-spreadsheet-records.md).

## Context

Applications export lists to Excel and import master data from it. The published pages already show both halves: a
handler that returns an `.xlsx` export as an `ElarionFile`, and an uploaded-file member declared with
`[UploadLimits(FileExtensions = [".xlsx", ".xls"])]`. What sits between the file and the handler is left to each
application, and under NativeAOT it is not simple (the research note has the survey):

- **No widely used library reads and writes under AOT.** SpreadCheetah declares AOT compatibility and writes only;
  Sylvan.Data.Excel declares it on its net8.0 target and reads `.xlsx`, `.xlsb` and `.xls`; the one library that
  declares it for both directions has almost no users and one maintainer.
- **Typed reading is where AOT breaks.** Sylvan's typed binder (`GetRecords<T>` in the separate Sylvan.Data package)
  compiles expression trees at run time. SpreadCheetah's source generator maps objects to rows for writing only.

An application that imports its master data from Excel needs more than typed rows, as one that hand-wrote about ten
such imports over a high-level spreadsheet library shows:

- **A preview before anything is saved.** The person sees every row with its values and its problems, then confirms.
  So a read must keep the rows that failed, not only report their errors.
- **Updates that change only what the file states.** A column a file does not carry (a trimmed sheet, an export older
  than the column) must leave the stored value alone, while an empty cell in a carried column clears it. Treating the
  two alike once wiped a contact field on every row a file updated. So a read must say which columns the file has.
- **Templates to fill in**, with a styled header and dropdown lists of the labels of choice columns that reach well
  below the data, so rows a person adds keep their pickers.
- **Messages in the application's language**, for the problems the framework finds as much as for the application's
  own rules; and values as people type them: a decimal comma or point, day-first dates, yes and no in their language.
- **Renamed columns** that still read from older files, and **worksheets in a foreign layout** (a bank's statement
  export) read as plain typed cells, with the same limits on hostile files.

What differs between applications is the row type, the wording, and what happens to the rows. The mapping, the
conversions, the validation of each row, the locating of problems and the connection to the file tiers
(`ElarionFile`, `ElarionStreamedFile`, `UploadedFile`) are the same everywhere, and Elarion already has the conventions
for them: `[SqlRecord]` (a generated mapping where an unsupported member is a build error, never a reflection fallback)
and DataAnnotations validation with field-keyed errors.

## Decision

`Elarion.Spreadsheets` is an opt-in package that depends on `Elarion.Abstractions`, `Microsoft.Extensions`
abstractions and Sylvan.Data.Excel, and bundles its generator. It does not depend on ASP.NET Core, EF Core or
`Elarion.Blobs`: it writes to and reads from streams, and the file tiers carry them.

1. **A spreadsheet record is a generated mapping in both directions.** `[SpreadsheetRecord]` on a `partial` class makes
   its properties the columns of a worksheet. `[SpreadsheetColumn]` names a header, its aliases (older headers that
   still read as it), an order, a number format, a width, and the labels of a boolean; `[SpreadsheetIgnore]` leaves a
   property out; `[Display(Name)]` is the header when no column name is given and the label of an enum member. The
   generator emits the code that writes a record as a row and reads a row into a record, plus a constant per column. A
   property of an unsupported type, a duplicate header or alias, an invalid worksheet name, a struct, or a member that
   collides with a generated one is a build error (`ELSHEET` diagnostics): if it builds, it maps, as with `[SqlRecord]`.
   The writer is one type, `SpreadsheetWorkbook`, with an options bag from the start.
2. **Elarion writes `.xlsx` itself.** A workbook holds a handful of fixed XML parts in a zip, and a writer driven by
   generated code needs only text, number, boolean and date cells, number formats, column widths, one header style and
   dropdown lists. It writes forward-only on the asynchronous zip API of .NET 10, so an export streams into a response
   body (`ElarionStreamedFile.FromWriter`, which already tolerates a zip entry's synchronous trailer flush) or into a
   blob without holding the workbook in memory; the only records it holds are the first 200, which it measures for the
   column widths before the columns are declared. Every worksheet has one fixed header style (bold, a light fill, a thin
   bottom border, frozen). Dates without a declared format use the built-in formats a spreadsheet application shows in
   its user's own notation. Enum columns and labelled boolean columns get a dropdown list of their labels on every
   written row and on at least the first 2,000, so writing no records is a template; a list longer than the 255
   characters a list formula holds goes on a hidden worksheet. Text columns carry the text number format on the same
   rows, so a spreadsheet application keeps a typed `01010` or a long reference as text instead of turning it into a
   number. A `DateTimeOffset` is ISO 8601 text with its offset,
   because a spreadsheet date has no time zone; a `DateTime` is written as its date and time whatever its `Kind`. A
   value a spreadsheet cannot hold (a date before 1900, `NaN`, an infinity, over-long text, too many rows) fails the
   write naming the row and the column. Text is always a text cell, never a formula. The output is deterministic for
   one package version on one runtime and platform (fixed entry timestamps, no document properties); compressed output
   is not promised stable across runtimes. The tests validate written files against the Open
   XML SDK's schema validator, a test-only dependency.
3. **Reading goes through Sylvan.Data.Excel, behind an internal interface.** It reads `.xlsx`, `.xlsb` and `.xls`,
   recognized from the content, takes the first visible worksheet and, as the header row, the first non-empty row,
   unless the read options name others. A workbook is opened once and can then be read as several worksheets and
   record types. Before the library sees a zip-based file, the reader inflates it once into a buffer while counting the
   bytes against the unpacked-size limit — declared sizes can lie, and the library inflates internally where no limit
   can be applied — and hands the library that buffer through its synchronous entry point, which reads it in place. The
   generated reader resolves the columns by header and alias once per
   worksheet and reads cells through the library's typed `DbDataReader` getters, never through Sylvan.Data's binder. No
   Sylvan type appears in the public API, so replacing the reader (a fork, another library, an own reader) changes
   neither a record nor a handler.
4. **The read result keeps every row and states the columns.** Each data row comes back with the row number the
   spreadsheet shows, the text of each mapped cell, its errors, and its record when it has none, so a preview can list
   every row; a row whose record is set says so to the compiler, so a handler needs no null-forgiving operator. The
   result says for each column of the record whether the file carries it, so an update can leave an unstated column
   alone; a missing column is an error only when its property is not nullable (ADR-0082), so a record whose key and
   name are non-nullable and whose other members are nullable serves both creating rows and updating them from a
   trimmed sheet. Every error of every row is reported: the row limit bounds the result, a separate error cap would hide
   problems from a preview. The application adds its own errors (an unknown reference, a duplicate key) to a row by
   column, and they are located and converted exactly like the package's.
5. **A file a user uploaded is input, not infrastructure.** A file that is no workbook, a missing or duplicated column,
   a value that does not convert and a row that fails validation are errors in the result, located by worksheet, row,
   column header and cell reference. Reading throws only on cancellation and on misconfiguration. This is the opposite
   of `ITypstCompiler` (ADR-0093), and deliberately so: a failed compilation is the application's fault, a broken
   upload is the user's, and the user needs every problem at once. The result converts to an `AppError.Validation`
   keyed by a field path the handler names, normally the uploaded-file member, worded by a formatter the handler may
   pass and capped at 100 messages plus one that counts the rest, so a broken large file does not become a
   multi-megabyte response.
6. **Every problem carries a code and its data.** The package's own errors have stable `spreadsheet.*` codes in the
   framework's `area.snake_case` form, with the data their wording needs (the header, the cell, the expected kind, the
   allowed labels, the limit) and an English default message, so an application words them in its language as it does
   upload refusals. A DataAnnotations violation carries `validation` and the validator's message, which the
   application already controls with `ErrorMessage` or resources.
7. **Text typed by hand is read by one lenient, culture-independent rule.** In a number column the rightmost of `,` and
   `.` is the decimal mark when both occur and a single one is the decimal mark otherwise, spaces are ignored, and an
   a separator that occurs more than once alone groups thousands in threes, and an integral column takes digits only.
   A date column takes ISO 8601 and day-first dotted dates (`d.M.yyyy`, `d.M.yy` in the 2000s, with an optional time). A boolean column takes boolean cells, numbers, `true`/`yes`/`x`/`1` and `false`/`no`/`0`.
   The read options add date formats and yes and no words; a `DateTimeOffset` column takes only ISO 8601 text that
   states an offset, never a date cell, so an import never depends on the server's time zone. A number cell reads at
   the 15 significant digits a spreadsheet shows, and as text without an exponent.
8. **Rows are validated through the validation seam, and the check is positive.** The DataAnnotations of a record run
   through `IRequestValidator` (ADR-0027) with the same generated, constant-constructed metadata as a handler request.
   `IRequestValidator` answers "valid" for a type it has no metadata for, and metadata is registered per module and
   only while the module is enabled, so "a validator is registered" is not enough: the seam gains a question whether it
   holds metadata for a type, and a record that carries validation attributes fails its read with an exception naming
   the cause when the answer is no. The spreadsheet package does not teach the core generator its attribute: a neutral
   marker in `Elarion.Abstractions`, which the generated record half carries, makes a type a validation root for the
   module that contains it, and the validation generator, which knows the modules, reports a validated root outside
   every module (`ELVAL004`) and a missing `Elarion.Validation` reference (`ELVAL002`). An import is never silently
   unchecked.
9. **A worksheet can be read without a record.** For a foreign layout the reader returns the non-empty rows of a
   worksheet as typed cells (blank, text, number, boolean, date, error) with their row numbers, under the same format
   detection, worksheet choice, cell rules and limits, plus a column limit. Finding the header row of such a layout is
   the application's.
10. **Reading is bounded.** The library reads the whole input into memory, so an import declares upload limits as any
    upload does. On top of that the reader refuses a workbook whose unpacked content exceeds a limit before unpacking
    it, stops after a maximum number of rows (keeping the rows before it) and, without a record, of columns, and never
   processes a DTD or an
    external entity. The defaults (256 MiB, 100,000 rows, 256 columns) are sized for the 1–10 node tier and are options.

Out of scope: styling beyond the fixed header style, formulas, merged cells, images, charts, comments, data validation
other than the dropdown lists of choice columns, password-protected workbooks, writing `.xls` or `.xlsb`, CSV, numbered
column groups mapped to one collection, and rendering a cell as the spreadsheet formats it. What an import does with a
row — create, update, skip, resolve references, commit all or nothing — stays the application's. An application that
needs a formatted report references a spreadsheet library itself; nothing in the package conflicts with one.

## Consequences

- An application adds the package and `[SpreadsheetRecord]` to a `partial` type. An export is one call that writes
  records to a stream, an `ElarionFile` or an `ElarionStreamedFile`, and a template is the same call with no records; an
  import is one call on the uploaded file's stream that returns every row, the stated columns and coded errors, from
  which the application builds its preview and its update.
- Elarion owns an `.xlsx` writer, and with it Excel's limits (sheet names, 32,767 characters in a cell, 1,048,576 rows,
  255 characters in a list formula), its 1900 leap-year date bug, the characters XML cannot carry, and the 15
  significant digits of a number cell. They are known and few, and the tests pin each one.
- The lenient rule reads `1.234` as 1.234, not 1234: a dot alone is a decimal mark. That is the price of one rule that
  needs no culture and accepts both notations a person in a comma-decimal locale types; a file written by the package
  never depends on it, because it writes numbers as number cells.
- One third-party runtime dependency remains: Sylvan.Data.Excel, at 0.x, with one maintainer and the AOT flag on its
  net8.0 target only. Its MIT license allows forking or vendoring it, and the internal interface keeps either a
  change inside the package. Revisit when it stalls (no release within a year, or a security report left open), or if
  `.xls` support is dropped, which would make an own `.xlsx` reader small enough to own.
- An import holds the uploaded file in memory and keeps every row of it in the result. That is fine for files of tens
  of megabytes and the default row limit at this tier; an import of millions of rows belongs to a dedicated pipeline.
- `Elarion.Abstractions` gains a validation-root marker and `IRequestValidator` a metadata question; both are neutral
  and usable by any future record kind that is validated outside a handler request.
- A third record generator joins the handler and `[SqlRecord]` generators. The conversions, the diagnostics and their
  tests are the bulk of the work, not the file format.
- The library flags are declarations, not proof: the package's tests include a NativeAOT publish that writes and
  reads records and must produce no trimming or AOT warning.

## Alternatives considered

- **SpreadCheetah for writing and Sylvan.Data.Excel for reading, both wrapped.** The first proposal. Once generated
  code emits the cell writes and the engine is hidden, SpreadCheetah's generator and styling API go unused, and what
  remains is the zip and XML writing — the cheapest part to own. Rejected: a dependency for the smallest part.
- **An own reader as well.** Reconsidered after a source read of the library: it already handles hidden sheets, date
  detection by custom formats, the 1904 system, cached formulas and Strict OOXML in three formats, and what it lacks
  (bounded inflation, DTD refusal, `veryHidden`) is one pre-pass the package owns in front of any reader. Removes the
  last dependency, but reading untrusted files from Excel, LibreOffice, Google
  Sheets, Numbers and business software is a long tail: shared strings with rich-text runs, dates recognized only by a
  built-in or custom number format, the 1904 date system, Strict OOXML namespaces, cells without references, cached
  formula results, the binary `.xls` format, plus zip-bomb and malformed-file hardening. Rejected for now; revisable on
  the triggers above or when the spike's go/no-go criteria in the plan fail, in which case an own `.xlsx` reader
  replaces the library and `.xls` and `.xlsb` are dropped, rather than keeping the library for the binary formats
  only.
- **SpreadSheetTasks for both directions.** The only library that declares AOT compatibility for reading and writing,
  but with one maintainer, almost no users and a 0.x-to-2.x jump within months — and it would still need Elarion's
  generator for typed records. Rejected.
- **A styling API, or exposing the engine so an application can style a worksheet written from records.** Either
  grows the package toward a report builder, and the second freezes a library's types into the public API. Rejected;
  one fixed header style covers exports and templates.
- **A documentation recipe instead of a package.** Cheapest, but every application would rewrite the reader, the
  conversions, the stated-column tracking and the error locating, which is where the bugs are. Rejected.
- **Exceptions for broken files.** Would match `ITypstCompiler`, but every import handler would translate parse
  exceptions into user errors, and the user would learn only the first problem. Rejected.
- **Returning only the records that passed, with a list of errors.** Simpler, but a preview has to show the failed rows
  with their values next to their problems. Rejected.
- **A cap on the number of errors.** Would bound the result once more, but hides problems from a preview that lists
  every row; the row limit already bounds it. Rejected.
- **Parsing text with a culture from the read options.** One culture accepts either `1.234,56` or `1234.56`, while a
  person in a comma-decimal locale types both, and a hand-filled template mixes them. Rejected for the lenient rule,
  with options for date formats and yes and no words.
- **Reading a date cell into a `DateTimeOffset` as UTC or as the server's local time.** Silently picks a time zone the
  person who typed the date never stated. Rejected: such a column takes ISO text with an offset.
- **Only records, no plain rows.** An application that reads foreign layouts would keep a second spreadsheet library
  for them. Rejected; plain rows reuse the reader, its cell rules and its limits.
- **The validation generator matching the spreadsheet attribute by name.** Simpler, but puts knowledge of an opt-in
  package into the core generator, and leaves two generators to agree on what "carries validation" means. Rejected for
  the neutral marker.
- **Trusting "a validator is registered".** Fails open for a disabled module or a record outside every module. Rejected
  for the positive metadata question.
- **A static writer beside a workbook type, and a reader per call.** Two writer entry points overlap, and a per-call
  reader parses an upload again for each worksheet. Rejected for one workbook type to write and an opened workbook to
  read.
- **Positional columns.** Simpler, but a reordered or extra column in a user's file would shift values silently.
  Rejected; columns match by header and alias.
- **Typed cell errors in the schema and the generated client.** Would let a client highlight cells, but adds an error
  contract before any client needs it. Rejected for now: the read result carries the codes and locations, and the
  application shapes its own preview from them.
- **A format-neutral tabular seam (CSV and `.xlsx` behind one interface).** The record mapping is format-neutral in
  shape, so a CSV writer can join the package later without changing a record. Not decided now, because no second
  format is in scope.
