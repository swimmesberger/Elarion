---
type: Specification
title: "Spec 0003: spreadsheet records"
description: "What Elarion.Spreadsheets must do: write typed records and fill-in templates as .xlsx, and read uploaded workbooks back into records with every row, every problem and every stated column reported, under NativeAOT."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-24T16:10:00Z }
status: stable
---
# Spec 0003: spreadsheet records

This spec describes the new opt-in package `Elarion.Spreadsheets`. Why it is shaped this way, and what was rejected,
is in [ADR-0108](../docs/decisions/0108-spreadsheet-records.md).

## Intent

A developer declares a spreadsheet record once — a `partial` type marked `[SpreadsheetRecord]` — and gets an `.xlsx`
export of it, a fill-in template with dropdown lists for its choices, and an import of it from an uploaded workbook, with
no hand-written mapping and no reflection, so the application still publishes with NativeAOT. An import reports every
row with its values and its problems, so the application can show a preview before anything is saved; it says which
columns the file contains, so an update leaves alone what the file does not state; and every problem carries a code and
its data, so the application words it in its own language. A worksheet in a foreign layout, such as a bank statement,
can be read as plain rows of typed cells with the same formats and limits.

## User scenarios

### Export records as an .xlsx file (P1)

- Given a spreadsheet record with text, number, date and boolean properties, when a handler returns a list of them as
  an `ElarionFile`, then the caller receives an `.xlsx` file whose first row holds the headers and each following row
  one record, with numbers as numbers, dates as dates and booleans as booleans.
- Given a property with `[SpreadsheetColumn("E-mail", Order = 1)]` and another with `[Display(Name = "Customer since")]`,
  when the records are written, then the headers read "E-mail" and "Customer since" and "E-mail" is the second column.
- Given a property marked `[SpreadsheetIgnore]`, when the records are written, then it has no column.
- Given a date column without a format, when the file is opened in a spreadsheet application set to an Austrian locale,
  then the dates show as `24.11.2026`, and in one set to a US locale as `11/24/2026`.
- Given an amount column with `Format = "#,##0.00"`, when the file is opened, then 1234.5 shows as `1,234.50` in the
  viewer's separators.
- Given a `DateTimeOffset` property, when the records are written, then its cells hold ISO 8601 text with the offset.
- Given a text value that starts with `=`, when the records are written and the file is opened, then the cell shows the
  text and is not evaluated as a formula.
- Given any export, when the file is opened, then the header row is bold on a light fill with a bottom border and stays
  in place while scrolling, and each column without a declared width is about as wide as its header and its values.
- Given 100,000 records produced one at a time, when an HTTP-only handler returns them as a streamed file, then the
  first bytes reach the client before the last record is produced, and the server's memory does not grow with the
  number of records.
- Given the same records written twice by the same package version on the same runtime, when the two files are
  compared, then they are byte-identical.
- Given a text holding the control character U+0001, when it is written and read back, then it reads unchanged.
- Given a computed get-only property, when the records are written, then it has a column; when the file is read, then
  that column is ignored.
- Given a `DateTime` property holding `default` (the year 1), a `double` holding `NaN`, or two worksheets named "Data"
  and "data", when the records are written, then the write fails with an exception that names the row and the column,
  or the worksheet.

### Hand out a template to fill in (P1)

- Given a spreadsheet record with an enum property whose members carry `[Display(Name)]` labels, when no records are
  written, then the file holds the header row, and the enum column offers the labels as a dropdown list in the first
  2,000 rows.
- Given a boolean property with `[SpreadsheetColumn(TrueLabel = "Ja", FalseLabel = "Nein")]`, when the records are
  written, then its cells show "Ja" or "Nein" and the column offers both as a dropdown list.
- Given an enum with sixty members whose labels are together longer than 255 characters, when the template is written,
  then the dropdown list still offers every label.
- Given an export of 5,000 records with an enum column, when the file is opened, then every row's cell in that column
  offers the dropdown list.
- Given a template's text column, when a person types `01010` or a 16-digit reference into it in a spreadsheet
  application and saves, then the file holds the typed text, and it reads back unchanged.

### Import records from an uploaded workbook (P1)

- Given an `.xlsx` file whose headers match the record's in a different order, with different case and surrounding
  spaces, and with one extra column, when it is read, then every row becomes a record with the right values and the
  extra column is ignored.
- Given a record whose column was renamed from "Mail" to "E-mail" with `Aliases = ["Mail"]`, when a file with the header
  "Mail" is read, then the column reads as "E-mail"; when a file carries both headers, then "E-mail" is read and "Mail"
  is ignored.
- Given the same data saved as `.xls` and as `.xlsb`, when each is read, then the records are the same as from the
  `.xlsx` file.
- Given a file whose name says `.xlsx` but whose content is `.xls`, when it is read, then it is read as `.xls`.
- Given a workbook whose first worksheet is hidden, when it is read, then the first visible worksheet is read.
- Given a worksheet with two empty rows and a title row above the headers, when it is read naming the header row, then
  the records come from the rows below it; without naming one, the first non-empty row is the header row.
- Given empty rows between and after the data, when the file is read, then they produce no rows and no errors, and the
  row numbers of the others are those the spreadsheet shows.
- Given a cell holding a formula, when the file is read, then the record holds the formula's last calculated value.
- Given a workbook that uses the 1904 date system, when it is read, then every date reads as the date the spreadsheet
  shows.
- Given one uploaded workbook with a "Contacts" and a "Bank accounts" worksheet, when the handler opens it once and
  reads each worksheet as its record type, then both read without the file being opened or parsed again.
- Given an enum column holding labels, member names and a label in other case, when it is read, then each reads as its
  member.
- Given an enum member `NaturalGas` with `[Display(Name = "Erdgas")]` and `[SpreadsheetChoice(Aliases = ["gas",
  "naturalgas"])]`, and a member `Heating` labelled "Wärme" with the alias "waerme", when a file holding "gas",
  "GAS ", "Erdgas" and "waerme" is read, then each reads as its member; when records are written, then the cells and the
  dropdown list show only the labels "Erdgas" and "Wärme".

### Preview an import before anything is saved (P1)

- Given a workbook with 40 rows of which 3 have problems, when it is read, then the result holds all 40 rows, each with
  its row number and the text of each of its cells; the 37 rows without problems hold their record, and the 3 others
  hold their errors and no record.
- Given a workbook in which row 13 has an invalid address in the "E-mail" column, which is column C, and the property
  carries `[EmailAddress(ErrorMessage = "Keine gültige E-Mail-Adresse.")]`, when it is read, then row 13 holds one error
  naming the column "E-mail", the cell C13 and that message.
- Given a workbook with 500 rows that all have an error, when it is read, then the result holds every one of the 500
  errors.
- Given a row whose key names no stored entry, when the handler adds its own error for that cell, then the error carries
  the worksheet, the row, the column and the cell reference like the package's own errors, and appears in the result and
  in its `AppError`.
- Given a result with errors, when the handler converts it to an `AppError` for its uploaded-file member `workbook`,
  then the caller receives a validation error keyed `workbook` whose messages name the row, the column and the cell.
- Given a result with 500 errors, when it is converted to an `AppError`, then the error holds 100 messages and one that
  says 400 more exist; given a formatter, the messages use the application's wording.

### Update only what the file states (P1)

- Given a record with optional columns "Phone" and "Notes" and a file that carries the "Phone" column but not "Notes",
  when it is read, then the result says that the file contains "Phone" and does not contain "Notes", without an error.
- Given the same file in which one row's "Phone" cell is empty, when it is read, then that row's record holds no phone
  and the result still says that the file contains "Phone", so the handler can tell a cleared value from an unstated
  one.
- Given a workbook without a column that a non-nullable property maps, when it is read, then the result holds one
  error naming the missing header and no rows.
- Given a record whose key and name are non-nullable and whose other properties are nullable, when a file carrying only
  the key, the name and "Phone" is read, then every row reads, and the result says which columns the file lacks.

### Report problems in the application's language (P1)

- Given a value "abc" in a number column, when the file is read, then the error carries the code
  `spreadsheet.invalid_value`, the expected kind "number", the column and the cell, and an English default message.
- Given an unknown label in an enum column, when the file is read, then the error carries `spreadsheet.invalid_value`
  and the allowed labels.
- Given a missing column, an empty required cell, a file that is no workbook, a file that unpacks too large and too
  many rows, when each is read, then the error carries `spreadsheet.column_missing`, `spreadsheet.required`,
  `spreadsheet.unreadable`, `spreadsheet.too_large` and `spreadsheet.too_many_rows` with the header, the cell or the
  limit as its data.
- Given a record that carries validation attributes, when it is read in an application that has not called
  `AddElarionValidation`, or whose module is disabled, then the read fails with an exception that names the cause.

### Values typed by hand are read leniently (P2)

- Given the texts `1.234,56`, `1,234.56`, `1234,56`, `1234.56` and `1 234,56` in a decimal column, when the file is
  read, then each reads as 1234.56; `1.234.567` reads as 1234567, and `1.23.4` is an error.
- Given the texts `24.11.2026`, `24.11.26`, `1.2.2026` and `2026-11-24` in a date column, when the file is read, then
  each reads as the date it states.
- Given the texts `x`, `yes`, `1`, `true`, `no`, `0` and `false` in a boolean column, and an application that adds
  `ja`, `j` and `nein`, when the file is read, then each reads as its boolean.
- Given a postal code stored as the number 1010 and a 16-digit reference stored as a number in text columns, when the
  file is read, then the first reads as `1010` and the second as its first 15 significant digits without an exponent,
  because that is all a number cell holds.
- Given a number 1234.56 that the file stores as the nearest binary fraction, when it is read into a decimal, then it
  reads as exactly 1234.56.
- Given a `DateTimeOffset` column holding `2026-11-24T10:00:00+01:00`, a date cell, and the text `24.11.2026 10:00`,
  when the file is read, then the first reads as that instant and the other two are errors, because they name no offset.

### The mapping is checked at build time (P2)

- Given a spreadsheet record with a property of an unsupported type, when the project builds, then the build fails
  with `ELSHEET001` naming the property, and marking it `[SpreadsheetIgnore]` fixes the build.
- Given a type marked `[SpreadsheetRecord]` that is not `partial`, when the project builds, then the build fails with
  `ELSHEET002`.
- Given two properties whose headers or aliases are equal ignoring case, when the project builds, then the build fails
  with `ELSHEET003` naming both.
- Given `[SpreadsheetRecord("Q1/Q2")]`, when the project builds, then the build fails with `ELSHEET004` naming the
  character a worksheet name may not contain.
- Given a spreadsheet record with `[Required]` on a property, declared outside the namespace of every module of its
  assembly, when the project builds, then the build fails with `ELVAL004` naming the record, because no module would
  validate it; in an assembly that does not reference `Elarion.Validation`, the build warns with `ELVAL002`.
- Given `[SpreadsheetRecord]` on a struct, when the project builds, then the build fails with `ELSHEET006`.
- Given a spreadsheet record with a property named `Columns`, when the project builds, then the build fails with
  `ELSHEET007` naming the member that collides with a generated one.
- Given an enum whose member carries the alias "gas" while another member is labelled or named "Gas", when the project
  builds, then the build fails with `ELSHEET008` naming both members.
- Given `[SpreadsheetColumn(TrueLabel = "Ja")]` on a boolean property without a `FalseLabel`, or both labels on a
  string property, when the project builds, then the build fails with `ELSHEET005` naming the property.
- Given an application published with NativeAOT that writes and reads spreadsheet records, when it is published, then
  the package causes no trimming or AOT warning, and the published application reads back the records it wrote.

### Read a foreign layout as plain rows (P2)

- Given a bank's statement export whose header row sits below a few lines of account details, when it is read without a
  record, then the result holds every non-empty row with its row number and its cells, each blank, text, number,
  boolean, date or error, and the application finds the header row itself.
- Given such a file with a hidden first worksheet, more columns than the read options allow, or more rows than they
  allow, when it is read, then the hidden worksheet is skipped, or the result holds one error naming the limit.

### Hostile files are bounded (P2)

- Given a small upload that expands to gigabytes when unpacked, when it is read, then the result holds one error that
  says the workbook is too large, and the server's memory stays bounded.
- Given a workbook whose XML declares an external entity, when it is read, then the entity is not resolved.

### Several worksheets in one workbook (P3)

- Given two spreadsheet record types, when both are written into one workbook, then it has one worksheet per type, in
  the order written, each named as its record declares.
- Given a workbook with several worksheets, when it is read naming the second worksheet, then the records come from
  that worksheet; naming a worksheet that does not exist gives one error that names it.

### Worksheets the application shapes per call (P2, added 2026-11-27)

- Given a schema whose columns are composed at run time (numbered slots, columns from a setting), when the application
  declares the worksheet's name and columns and writes rows of values, then the worksheet has the declared headers,
  number and date formats, widths and dropdown lists, as a record's would.
- Given a row that holds the label "Total" in a date column, or a second block of other types below the first, when it
  is written, then each value is a cell of its own type, and a value of the column's kind keeps the column's format.
- Given a record with a column only some callers may see, when a caller without it exports, then the application leaves
  the column out for that call, and the other columns close up.
- Given a record export, when the application adds footer rows, then they follow the records, and the dropdown lists
  stop above them.
- Given a text column whose dropdown labels are data, when the record is written, then the column offers those labels;
  when it is read, then it takes any text, or, if declared strict, only the labels, reporting others with the allowed
  labels.
- Given a workbook already in memory, when it is read as plain rows, then the read is synchronous, and each number or
  date cell carries the number format the file gives it.
- Given a run-time column whose values are text in some rows and numbers in others, when it is written, then each value
  keeps its own type and the column has no format of its own (FR-081).
- Given an export that needs a title above its header, when the application adds title rows, then they stand above the
  header row, which stays in place while scrolling, and a read that names the header row reads the records (FR-082).
- Given a worksheet of lookup values, when the application writes it hidden, then the workbook opens on its first
  visible worksheet and a reader skips the hidden one (FR-083).
- Given a run-time worksheet with a total row, when the application writes the total as a footer row, then the dropdown
  lists stop above it (FR-084).
- Given a one-line in-memory export, when the application passes worksheet options, then they apply as they do to an
  added worksheet (FR-085).
- Given a caller without an asynchronous context, when it writes a workbook into memory, then the write is synchronous
  and blocks on no task (FR-086).

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

- FR-001: The feature MUST ship as the opt-in package `Elarion.Spreadsheets`, which MUST NOT depend on ASP.NET Core,
  EF Core or `Elarion.Blobs` and MUST NOT expose a type of a third-party library in its public API.
- FR-002: A type MUST opt in with `[SpreadsheetRecord]` and MUST be `partial`, otherwise the build MUST fail with
  `ELSHEET002`.
- FR-003: Every public instance property with a getter MUST become a column, in declaration order unless
  `[SpreadsheetColumn(Order = …)]` sets another, and `[SpreadsheetIgnore]` MUST leave a property out.
- FR-004: A column's header MUST be the name given in `[SpreadsheetColumn]`, else the `[Display(Name)]` of the property,
  else the property's name.
- FR-005: A file header equal to one of a column's `[SpreadsheetColumn(Aliases = …)]` MUST read as that column, unless
  the file also carries the column's own header, in which case the alias column MUST be ignored.
- FR-006: The mapping MUST support `string`, `bool`, the integral and floating-point types, `decimal`, `DateTime`,
  `DateTimeOffset`, `DateOnly`, `TimeOnly`, `Guid`, enums and the nullable forms of these, and any other property type
  MUST fail the build with `ELSHEET001` naming the property.
- FR-007: Two columns whose headers or aliases are equal ignoring case MUST fail the build with `ELSHEET003`.
- FR-008: A worksheet name in `[SpreadsheetRecord]` that is empty, longer than 31 characters, contains one of
  `[ ] : * ? / \` or starts or ends with an apostrophe MUST fail the build with `ELSHEET004`, and a record without one
  MUST use its type name.
- FR-009: An enum value MUST be written as the `[Display(Name)]` of its member, else the member's name, and MUST read
  from either, ignoring case and surrounding whitespace.
- FR-010: A boolean property with `[SpreadsheetColumn(TrueLabel = …, FalseLabel = …)]` MUST be written as those labels
  and MUST read them besides the values of FR-045.
- FR-011: Writing and reading MUST NOT use reflection, so that an application published with NativeAOT gets no
  trimming or AOT warning from the package.
- FR-012: Writing MUST produce an `.xlsx` workbook with one worksheet per record type, in the order written, whose
  first row holds the headers and each following row one record.
- FR-013: Numbers MUST be written as number cells, booleans without labels as boolean cells, `DateTime`, `DateOnly` and
  `TimeOnly` as date cells, and text, `Guid`s, enums and labelled booleans as text cells.
- FR-014: A `DateTimeOffset` MUST be written as ISO 8601 text with its offset.
- FR-015: Text MUST be written as a text cell and never as a formula, whatever it starts with.
- FR-016: Text MUST read back unchanged after writing, including characters that XML cannot carry.
- FR-017: A date, date-and-time or time column without a declared format MUST use the format that a spreadsheet
  application shows in its user's own date and time notation.
- FR-018: `[SpreadsheetColumn(Format = …)]` MUST set the number format of a number or date column, and
  `[SpreadsheetColumn(Width = …)]` MUST set the column's width in characters.
- FR-019: A column without a declared width MUST be as wide as the longest of its header and the text of its values in
  the first 200 records, at most 80 characters.
- FR-020: The header row MUST be bold on a light fill with a thin bottom border, and MUST stay in place while scrolling.
- FR-021: An enum column and a labelled boolean column MUST offer their values as a dropdown list in every written row
  and in at least the first 2,000 data rows, whatever the number and the length of the values.
- FR-022: Writing no records MUST produce a template with the header row, the column formats and widths, and the
  dropdown lists.
- FR-023: Writing MUST be forward-only to a stream and MUST NOT hold more of the workbook in memory than the records it
  measures for the column widths.
- FR-024: Writing MUST be able to produce the workbook as an `ElarionFile` and as an `ElarionStreamedFile`, with the
  `.xlsx` content type and a file name the caller gives.
- FR-025: The same records MUST produce byte-identical files with the same package version on the same runtime and
  platform.
- FR-026: A value a spreadsheet cannot hold — text longer than 32,767 characters, a row beyond 1,048,576, a date
  before 1900-01-01, `NaN` or an infinity — MUST fail the write with an exception that names the row and the column.
- FR-027: Two worksheets of one workbook whose names are equal ignoring case MUST fail the write with an exception.
- FR-028: Reading MUST accept `.xlsx`, `.xlsb` and `.xls` workbooks and MUST recognize the format from the content,
  not from the file name.
- FR-029: Reading MUST read the first visible worksheet unless the read options name a worksheet.
- FR-030: Reading MUST take the headers from the first non-empty row unless the read options name a row.
- FR-031: Reading MUST match columns by header ignoring case and surrounding whitespace, MUST NOT depend on column
  order, and MUST ignore columns that no property maps.
- FR-032: A header that the file carries twice and that a property maps MUST be one error that names it, and the result
  MUST then hold no rows.
- FR-033: A row whose mapped cells are all empty MUST be skipped without an error, and every other row MUST keep the row
  number the spreadsheet shows.
- FR-034: The result MUST hold every data row read, each with its row number, the text of each mapped cell, its errors,
  and its record when it has no error.
- FR-035: The result MUST tell for every column of the record whether the file contains it, and a missing column whose
  property is nullable MUST NOT be an error.
- FR-036: A missing column whose property is not nullable MUST be one error that names the header, and the result MUST
  then hold no rows.
- FR-037: The text of a cell MUST be its text for a text cell, its value of at most 15 significant digits in invariant
  notation without an exponent for a number cell, ISO 8601 for a date cell, `true` or `false` for a boolean cell, and
  empty for a column the file lacks.
- FR-038: A number cell MUST read as the value of at most 15 significant digits that a spreadsheet shows, and an
  integral property MUST reject a number with a fraction or outside its range.
- FR-039: A number cell MUST read as a date or time when its number format, built-in or custom, is a date or time
  format, in the date system (1900 or 1904) the workbook declares.
- FR-040: A formula cell MUST read as its cached value, and a formula MUST never be evaluated.
- FR-041: An empty cell MUST read as `null` for a nullable property and MUST be a `spreadsheet.required` error for a
  non-nullable one.
- FR-042: A property that cannot be set MUST be written and MUST NOT be read.
- FR-043: A text cell in a decimal or floating-point column MUST be read ignoring spaces, with the rightmost of `,` and
  `.` as the decimal mark when both occur, a separator that occurs more than once alone as thousands grouping in groups
  of three, and a single `,` or `.` as the decimal mark otherwise, and a text cell in an integral column MUST hold only
  digits and an optional sign.
- FR-044: A text cell in a date or date-and-time column MUST be read as ISO 8601, as `d.M.yyyy` or `d.M.yy` (a
  two-digit year in the 2000s) with an optional `H:mm` or `H:mm:ss` time, or in a format the read options add.
- FR-045: A boolean column MUST read boolean cells, a number cell as `true` when it is not zero, and the texts `true`,
  `yes`, `x` and `1` as `true` and `false`, `no` and `0` as `false`, ignoring case, plus words the read options add.
- FR-046: A `DateTimeOffset` column MUST read only ISO 8601 text that states an offset, and a date cell or a text
  without an offset in it MUST be a `spreadsheet.invalid_value` error.
- FR-047: Each record read MUST be validated with its DataAnnotations through the registered request validator, with
  the same messages a handler request would get.
- FR-048: Reading a record that carries validation attributes while no validation metadata for it is registered —
  no request validator, or the record's module disabled — MUST throw an exception that names the cause.
- FR-049: Reading MUST return the rows and the errors together, and MUST throw only on cancellation and on
  misconfiguration, never because of what the file contains.
- FR-050: Every error MUST carry a stable code, the data its wording needs, and an English default message.
- FR-066: The codes MUST be `spreadsheet.unreadable`, `spreadsheet.too_large`, `spreadsheet.too_many_rows`,
  `spreadsheet.too_many_columns`, `spreadsheet.no_visible_worksheet`, `spreadsheet.worksheet_not_found`,
  `spreadsheet.column_missing`, `spreadsheet.column_duplicated`, `spreadsheet.required` and
  `spreadsheet.invalid_value` (with the expected kind, and the allowed values of a choice), and `validation` for a
  DataAnnotations violation, whose message is the validator's.
- FR-051: An error about a cell MUST carry the worksheet, the row number, the column header and the cell reference, and
  an error about the file or a column MUST say which.
- FR-052: Reading MUST report every error of every row it reads.
- FR-053: A file that cannot be read as a workbook, including a truncated or password-protected one, MUST be exactly
  one error and no rows.
- FR-067: A workbook without a visible worksheet, or a worksheet name that does not exist, MUST be exactly one error and
  no rows.
- FR-054: The result MUST convert to an `AppError.Validation` whose field errors are keyed by a field path the caller
  names, or the empty key when it names none, with at most 100 messages, each worded by the caller's formatter or else
  naming the row, the column and the cell, and one closing message that counts the rest.
- FR-055: Reading MUST refuse with one error a workbook whose unpacked content exceeds the limit of the read options,
  256 MiB by default, without unpacking more than the limit.
- FR-056: Reading MUST stop with one error after the maximum number of data rows of the read options, 100,000 by
  default, keeping the rows read before it and not counting skipped empty rows.
- FR-057: Reading MUST NOT process document type definitions or resolve external entities.
- FR-058: Reading without a record MUST return the non-empty rows of a worksheet, each with the row number the
  spreadsheet shows and its cells, each cell blank, text, number, boolean, date or error.
- FR-059: Reading without a record MUST apply the formats, the worksheet choice, the cell rules of FR-037 to FR-040 and
  the limits of reading records, and MUST stop with one error after the maximum number of columns of the read options,
  256 by default.
- FR-060: `TrueLabel` and `FalseLabel` on a property that is not a boolean, or only one of the two, MUST fail the build
  with `ELSHEET005` naming the property.
- FR-061: A spreadsheet record that carries validation attributes and lies outside the namespace of every module of its
  assembly MUST fail the build with `ELVAL004` naming the record.
- FR-062: `[SpreadsheetRecord]` on a struct MUST fail the build with `ELSHEET006`.
- FR-063: A text column MUST carry the text number format on every written row and on the rows its dropdown lists
  cover, so that a value typed into it stays text.
- FR-064: A `DateTime` MUST be written as its date and time, whatever its `Kind`.
- FR-065: A spreadsheet record that carries validation attributes in an assembly that does not reference
  `Elarion.Validation` MUST raise the build warning `ELVAL002`.
- FR-068: The application MUST be able to add its own errors to a row, located by column like the package's own, and
  they MUST appear in the result's errors and in its `AppError`.
- FR-069: One workbook MUST be readable as several worksheets and record types after it has been opened once.
- FR-070: A record member whose name collides with a generated member MUST fail the build with `ELSHEET007`.
- FR-071: An enum value MUST also read from each alias that `[SpreadsheetChoice(Aliases = …)]` gives its member,
  ignoring case and surrounding whitespace, and an alias MUST never be written or offered in a dropdown list.
- FR-072: Two members of one enum whose labels, names or aliases are equal ignoring case MUST fail the build with
  `ELSHEET008` naming both.
- FR-073: The application MUST be able to write a worksheet whose name and columns (header, kind, number format, width,
  dropdown labels) it declares at run time, from rows of values in column order, synchronous or asynchronous, with the
  header, formats, widths, dropdown lists, streaming and limits of a record's worksheet.
- FR-074: In such a row and in a footer row, each value's type MUST decide its cell, and the column's format MUST apply
  only to a value of the column's kind, or of its family when the column declares a format; a value of an unsupported
  type, or more values than columns, MUST fail the write naming the row.
- FR-075: A record worksheet MUST accept footer rows of such values below the records, and its dropdown lists MUST stop
  above them.
- FR-076: A record worksheet MUST accept, per call, the columns to include and to exclude by property name, keeping the
  record's order; a name that matches no column MUST fail before anything is written, leaving the workbook usable.
- FR-077: A `string` property MUST be able to take its dropdown labels from a static `IReadOnlyList<string>` member of
  its record, named in `[SpreadsheetColumn(Choices = …)]`; a property that is not a string, or a member that is missing,
  not static, not public or internal, or of another type, MUST fail the build with `ELSHEET009`.
- FR-078: Such a column MUST read any text by default, and with `StrictChoices` only its labels (ignoring case and
  surrounding white space), reporting other text as `invalid_value` with the labels as allowed values.
- FR-079: A workbook already in memory MUST be readable as plain rows synchronously, without copying memory backed by an
  array.
- FR-080: A number or date cell read without a record MUST carry the number format the file gives it.
- FR-081: A run-time worksheet MUST be able to declare a general column, whose values each take their own type and
  default format and which carries no column format; text in it MUST NOT take the text format.
- FR-082: A worksheet MUST accept, per call, title rows of per-value cells above its header row; the frozen pane and
  the dropdown lists MUST follow the moved header row, and a write error MUST name the row the spreadsheet shows.
- FR-083: A worksheet MUST be writable hidden; the workbook MUST open on its first visible worksheet, and a workbook
  whose worksheets are all hidden MUST fail when it is completed.
- FR-084: A run-time worksheet MUST accept the per-call worksheet options of a record worksheet (name, title rows,
  footer rows, hidden); a column filter for it MUST fail before anything is written.
- FR-085: The one-line in-memory export of records MUST accept the per-call worksheet options.
- FR-086: A workbook of one worksheet, from records or a run-time definition, MUST be writable into memory
  synchronously without blocking on a task.

## Success criteria

- SC-001: A file written for every supported type, and a template, opens in Excel, LibreOffice Calc and Google Sheets
  without a repair prompt, with numbers, dates and booleans shown as such and the dropdown lists working.
- SC-002: Every supported type, written and read back, yields records equal to the ones written, except for the
  documented precision of decimals beyond 15 significant digits.
- SC-003: An application published with NativeAOT that writes and reads spreadsheet records gets zero trimming or AOT
  warnings from the package.
- SC-004: One read of a workbook with several broken rows reports every problem, each with a location a person can find
  in the spreadsheet.
- SC-005: Exporting 100,000 records as a streamed file allocates no more memory than exporting 1,000.
- SC-006: An application can word every problem of an import in its own language without parsing a message.
- SC-007: An application that hand-wrote an import with a preview, updates of stated columns only, and a template with
  dropdown lists can replace its mapping and parsing code with spreadsheet records and keep its behavior.

## Out of scope

- Styling beyond the fixed header style: fonts, colors, borders and conditional formatting of data cells.
- Formulas, merged cells, images, charts, comments, hyperlinks, and data validation other than the dropdown lists of
  choice columns.
- Password-protected workbooks, beyond reporting that they cannot be read.
- Writing `.xls` or `.xlsb`, and reading or writing CSV.
- Custom value converters; a computed get-only property covers an export, and a text property converted in the
  handler covers an import.
- Numbered column groups ("Phone 1", "Phone 2", …) mapped to one collection property; a record declares them as
  separate properties.
- Showing a cell's value as the spreadsheet formats it; a preview formats the record's values or uses the cell text,
  and a plain-rows read hands over the cell's number format (FR-080) for an application that renders it itself.
- A record property whose type changes from row to row; footer rows and run-time worksheets carry values of any type.
- Deciding what an import does with a row (create, update, skip), resolving references between worksheets, and
  committing all or nothing: those are the application's.
- Enumerating the rows of an import one at a time without collecting them.
- A spreadsheet error contract in the schema and the generated TypeScript client.

## Decisions

- [ADR-0108](../docs/decisions/0108-spreadsheet-records.md): the package, its own writer, the library behind the
  reader, the read result, the lenient text rules, plain rows.
- [ADR-0027](../docs/decisions/0027-declarative-request-validation.md): DataAnnotations through `IRequestValidator`.
- [ADR-0082](../docs/decisions/0082-requiredness-comes-from-nullability-in-both-directions.md): requiredness comes
  from nullability.
- [ADR-0098](../docs/decisions/0098-streamed-files-for-http-only-endpoints.md) and
  [ADR-0105](../docs/decisions/0105-uploaded-files-are-handler-parameters.md): the streamed-file and uploaded-file tiers
  an export and an import use.
