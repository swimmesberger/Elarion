---
type: Research note
title: "XLSX libraries for NativeAOT: which .NET readers and writers are AOT-compatible and maintained"
description: "A survey of .NET spreadsheet libraries for NativeAOT compatibility and maintenance, and where typed row mapping breaks under AOT; input for ADR-0108."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-24T14:45:00Z }
status: draft
sources:
  - https://github.com/sveinungf/spreadcheetah
  - https://github.com/MarkPflug/Sylvan.Data.Excel
  - https://github.com/MarkPflug/Sylvan
  - https://github.com/justybase/SpreadSheetTasks
  - https://github.com/mini-software/MiniExcel
  - https://github.com/dotnet/Open-XML-SDK
  - https://github.com/ExcelDataReader/ExcelDataReader
  - https://github.com/ClosedXML/ClosedXML
  - https://github.com/salvois/LargeXlsx
  - https://doc.tmssoftware.com/flexcel/net/tips/native-aot.html
  - https://devblogs.microsoft.com/dotnet/creating-aot-compatible-libraries/
---
# XLSX libraries for NativeAOT

- Date: 2026-11-24
- Status: Research input for [ADR-0108](../decisions/0108-spreadsheet-records.md). This is not a decision.
- Question: is there a well-maintained .NET library that reads and writes `.xlsx` under NativeAOT, and if not, which
  parts would a framework package have to own?
- Method: each library's project file read on its default branch for `IsAotCompatible` and `IsTrimmable`; the latest
  release, its target frameworks and its dependencies from the NuGet registration API; activity from the GitHub API.
  A declared flag means the library's own build runs the AOT analyzers; it is not proof that a published application
  is free of warnings. No library was published with NativeAOT for this note. **Inference** marks my reading.

## Summary

1. **No library both reads and writes, declares AOT compatibility and is widely used.** SpreadCheetah (write only)
   and Sylvan.Data.Excel (read, plus a basic writer) declare it; SpreadSheetTasks declares it for both directions but
   has almost no users and one maintainer.
2. **Typed rows are where AOT breaks.** Sylvan's typed binder (`GetRecords<T>` in Sylvan.Data) compiles expression
   trees at run time and carries no AOT annotations. SpreadCheetah's source generator maps objects to rows for writing
   only. A typed record that is written and read back under AOT exists in no surveyed library.
3. **Sylvan.Data.Excel does not depend on Sylvan.Data.** Its `DbDataReader` with typed getters is usable without the
   reflection binder, so a generator can read rows through it.
4. **Writing `.xlsx` needs little from a library.** .NET 10 ships an asynchronous zip API, and Elarion's
   `ElarionStreamedFile.FromWriter` already tolerates the synchronous trailer flush of a `ZipArchive` entry.
   **Inference:** a writer limited to text, numbers, booleans and dates is a small amount of code on the base library.

## Libraries

| Library | Direction | AOT in its project file | Latest release | Activity (2026-11-24) |
| --- | --- | --- | --- | --- |
| SpreadCheetah | write | `IsAotCompatible` from net8.0, `IsTrimmable` from net6.0 | 1.29.0, 2026-11-12 | 720 stars, 9 open issues, MIT |
| Sylvan.Data.Excel | read `.xlsx`, `.xlsb`, `.xls`; basic write | `IsAotCompatible` on its net8.0 target only | 0.5.8, 2026-08-10 | 362 stars, one maintainer, 0.x, MIT |
| SpreadSheetTasks | read and write `.xlsx`, `.xlsb` | `IsAotCompatible`; the repository holds an AOT smoke test | 2.2.0, 2026-11-11 | 4 stars, one maintainer, MIT |
| DocumentFormat.OpenXml | both, low level | `IsTrimmable` from net6.0; no `IsAotCompatible` | 3.5.1, 2026-03-18 | Microsoft, active |
| MiniExcel | both | none; maps objects by reflection | 2.0.0, 2026-11-23 | 3.6k stars, very active |
| ExcelDataReader | read | none (4.0.0-beta1 included) | 4.0.0-beta1, 2026-10-27 | active |
| ClosedXML | both | none; targets netstandard2.0 and 2.1 only | 0.105.1, 2026-07-25 | active, many dependencies |
| LargeXlsx | write | none; targets netstandard2.0 and netcoreapp3.1, depends on SharpCompress | 2.0.2, 2026-07-26 | 283 stars |

FlexCel documents that its reporting engine is not fully compatible with NativeAOT.

## Details that matter for a framework package

- **SpreadCheetah** writes forward-only to any stream, which suits a response body. Its source generator
  (`[WorksheetRow(typeof(T))]` on a `WorksheetRowContext`) produces rows from objects and has no reading
  counterpart. Its targets are netstandard2.0, net8.0, net10.0 and net11.0; on .NET it has no dependencies.
- **Sylvan.Data.Excel** has no NuGet dependencies. `ExcelDataReader.CreateAsync(Stream, …)` copies the whole input
  into a pooled memory stream before reading (`ExcelDataReader.cs`, around line 190), so an asynchronous read holds
  the file in memory. Only its net8.0 target sets `IsAotCompatible`; a net10.0 or net11.0 consumer resolves that asset.
- **Sylvan.Data** (the separate binder package) builds an `Expression.Lambda` per record type and calls `Compile()`
  (`CompiledDataBinder.cs`, around line 498); its project sets neither `IsAotCompatible` nor `IsTrimmable`.
- **Sylvan.Data.Excel's boundary** (source read of 0.5.8): it skips hidden worksheets by default
  (`ExcelDataReaderOptions.ReadHiddenWorksheets`) in all three formats, but its `.xlsx` reader compares the sheet
  `state` to `"hidden"` only, so a `veryHidden` sheet counts as visible (`XlsxWorkbookReader.cs`, around line 103). It
  detects dates by built-in and custom number formats, honours the 1904 system, reads cached formula values without
  evaluating them, and maps Strict OOXML. It opens the zip as a plain `ZipArchive` with no size check, loads
  `workbook.xml`, `styles.xml` and the relationship parts with `XmlDocument.Load` (which parses DTDs), and throws bare
  framework exceptions (`InvalidDataException`, `XmlException`, …) on broken input. No handling of encrypted `.xls`
  (BIFF `FilePass`) was found.
- **Spike, 2026-11-26** (the plan of spec 0003 has the full table): on .NET 10 with NativeAOT the library publishes
  with zero trimming or AOT warnings when the whole assembly is rooted. Its `.xls` reader fails in a static
  initializer unless `CodePagesEncodingProvider` is registered, and then stays broken for the process. .NET's
  `ZipArchive` ends an entry's stream at the size the central directory declares, so a lying size truncates the part
  instead of inflating past it. An entity bomb in `workbook.xml` is stopped by .NET's default entity cap after about
  170 MB of allocation.
- **.NET 10** reference assemblies (`Microsoft.NETCore.App.Ref` 10.0.2) contain `ZipArchive.CreateAsync`,
  `ZipArchive.DisposeAsync` and `ZipArchiveEntry.OpenAsync`.

## Sources

- SpreadCheetah: <https://github.com/sveinungf/spreadcheetah>, its NuGet page <https://www.nuget.org/packages/SpreadCheetah>
- Sylvan.Data.Excel: <https://github.com/MarkPflug/Sylvan.Data.Excel>; Sylvan.Data: <https://github.com/MarkPflug/Sylvan>
- SpreadSheetTasks: <https://github.com/justybase/SpreadSheetTasks>, <https://www.nuget.org/packages/SpreadSheetTasks>
- MiniExcel: <https://github.com/mini-software/MiniExcel>
- Open XML SDK: <https://github.com/dotnet/Open-XML-SDK>
- ExcelDataReader: <https://github.com/ExcelDataReader/ExcelDataReader>
- ClosedXML: <https://github.com/ClosedXML/ClosedXML>
- LargeXlsx: <https://github.com/salvois/LargeXlsx>
- FlexCel and NativeAOT: <https://doc.tmssoftware.com/flexcel/net/tips/native-aot.html>
- Creating AOT-compatible libraries: <https://devblogs.microsoft.com/dotnet/creating-aot-compatible-libraries/>
