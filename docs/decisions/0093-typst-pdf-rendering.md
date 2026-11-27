---
type: Architecture Decision Record
title: "ADR-0093: Typst PDF rendering is a framework package — the pinned CLI, bundled by the build, one sandboxed workspace per document"
description: "Typst PDF rendering is a framework package — the pinned CLI, bundled by the build, one sandboxed workspace per document."
status: draft
---
# ADR-0093: Typst PDF rendering is a framework package — the pinned CLI, bundled by the build, one sandboxed workspace per document

- Status: Proposed
- Date: 2026-11-18
- Related: [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node tier the defaults are sized for),
  [ADR-0039](0039-binary-file-responses.md) (the small-file tier `TypstPdf.ToElarionFileAsync` feeds),
  [ADR-0076](0076-web-push.md) (the "several apps built the same thing" precedent),
  and the [PDF documents](../capabilities/pdf-documents.mdx) capability page.

## Context

Several consuming applications generate business documents (invoices, quotes, inspection protocols, test reports)
with Typst, and each had built the integration itself. The three copies shared the shape — serialize a model to JSON,
run `typst compile`, return or store the PDF — and diverged exactly where the problems are:

- **Acquisition.** One installed the binary in its Dockerfile with `curl`, one relied on whatever `typst` was on `PATH`
  (with a startup warning when it was not), one downloaded a pinned, checksum-verified release in an MSBuild target and
  copied it into the output for the executable's runtime identifier. Only the last gave the same binary in development,
  CI, test hosts, and production, and needed nothing on the machine.
- **The sandbox.** Typst reads files only below its project root. Two copies made the template directory the root and
  wrote each job's data into a `tmp/` folder inside it — so the deployed template tree had to be writable, and a
  crashed job left files there. The third gave every compilation its own temporary root and shipped the templates as a
  Typst *package* on `--package-path`, which every root may import and none may write.
- **Process handling.** Draining stdout and stderr concurrently (reading after the exit deadlocks once diagnostics
  exceed a pipe buffer), killing the process tree on timeout or cancellation, observing abandoned pipe reads, keeping
  diagnostics out of HTTP responses, deterministic fonts, and a startup check were each present in some copies and
  missing in others.
- **Images.** Typst aborts the whole document on an image it cannot decode; one copy validated user photos
  structurally before handing them over, the others let a truncated upload fail the document.

None of this is domain logic. The document's layout, its data model, and where the PDF goes are.

## Decision

`Elarion.Typst` is an opt-in package that depends only on `Elarion.Abstractions` and `Microsoft.Extensions` packages.

1. **The CLI, not a native binding.** Typst is driven through its command-line compiler. It is the interface Typst
   versions and documents, it keeps a crashing or runaway compilation out of the application process, and it lets one
   pinned binary serve every platform without a native library per RID in the package. The cost — a process start per
   document, tens of milliseconds — is noise next to the compilation itself at the documents-on-demand rate of the
   target tier.
2. **The build acquires the binary; the runtime never downloads.** The package's `buildTransitive` targets download
   the pinned release once per version and platform into a per-user cache, verify it against SHA-256 checksums shipped
   in the package, and copy it to `typst/bin/` of every executable that references the package — directly or through a
   library — for that executable's `RuntimeIdentifier`. Executables include test projects, so tests run against the
   real binary without CI setup. A different version needs an explicit checksum; an air-gapped build points at a
   pre-fetched binary. At runtime the bundled binary is the default, `PATH` the fallback, configuration the override.
3. **Templates are a Typst package; each compilation gets its own root.** `ITypstCompiler` creates a fresh workspace
   per document (`main.typ`, `data.json`, `assets/`), passes it as `--root`, and passes the application's package tree
   as `--package-path`. The template tree is read-only, concurrent compilations cannot see each other's files, and the
   workspace is removed when the compilation ends. Because a package cannot read the root, `TypstSource.Package`
   generates the main file that reads `data.json` and the assets and passes them to the entry point — one calling
   convention, `#let name(data, assets: (:))`, for every document. Named arguments (`WithArgument`) select a variant;
   they are emitted as escaped Typst literals (string, bool, integer, exact `decimal`), never as raw code.
   `TypstSource.Main` remains for full control.
4. **Configuration binds without reflection.** `AddElarionTypst(IConfiguration)` binds `TypstOptions` through the
   configuration-binding source generator, once at registration, so deployment overrides (`Typst__ExecutablePath`)
   need no application code and the package stays NativeAOT-compatible.
5. **Bounded and observable.** On .NET 11 each child is started with `KillOnParentExit` (Windows, Linux: a crashed
   host leaves no Typst process behind), `InheritedHandles = []` (it never holds the host's sockets or files open), and a
   null standard input, and runs through `Process.RunAndCaptureTextAsync`, which drains both pipes concurrently and kills
   it on cancellation; the exit status names a terminating signal, so an out-of-memory kill is reported as `SIGKILL`.
   The .NET 10 build keeps the equivalent hand-written drain-and-kill. At most `MaxConcurrency` processes run at once
   (the rest wait, cancellably); each has a
   timeout after which its process tree is killed; the options are the only configuration a compilation sees
   (inherited `TYPST_*` and `SOURCE_DATE_EPOCH` variables are removed); system fonts are hidden by default. Durations,
   queue time, and outcomes are recorded through `TypstTelemetry`; diagnostics go to the log.
6. **Failures throw.** A compilation failure is an infrastructure fault, like a failed blob write, so `ITypstCompiler`
   throws `TypstException` subtypes (unavailable, compilation, timeout) rather than returning `Result`. The
   application's own document service translates them into a generic `AppError` at the handler boundary, keeping
   Typst's diagnostics — which quote server paths — out of responses.
7. **The output is a stream, not a byte array.** `TypstPdf` wraps the output file with delete-on-close, so a document
   streams into a blob store without being materialized; `ToElarionFileAsync` buffers it for the small-file tier.
8. **A startup check.** By default a missing executable or configured directory refuses the start, and
   `typst --version` runs beside the start (not on its path) and stops the host when it fails. `Warn` and `None` exist
   for hosts where PDFs are optional.

Out of scope: a template DSL or C#-side layout model, HTML-to-PDF, PNG/SVG output, and storage — the application
decides where a PDF goes (`IBlobStore`, a response, an e-mail).

## Consequences

- An application adds a package reference, an `ElarionTypstAssetsDirectory` property, and `AddElarionTypst()`; the
  Dockerfile install step, the process wrapper, the availability probe, and the temp-directory handling go away.
- The Typst version is pinned by the framework release. Moving to a new Typst release is a package update (the
  checksum table and the default version change together), or a local override with its checksum.
- Templates must be restructured as a package with function entry points. That is a one-time move, and it is what makes
  the template tree read-only and shareable between workspaces.
- Builds of executables need network access once per Typst version and platform, or a pre-fetched binary.
- A `Process` per document is not free. At the 1–10 node tier with documents rendered on demand this is irrelevant;
  a workload rendering thousands of documents per minute should batch them into one Typst document or move rendering to
  a dedicated worker.

## Alternatives considered

- **A native Typst binding (a Rust library over P/Invoke or a WebAssembly build).** Avoids the process start, but ties
  the package to native assets per platform or a WebAssembly runtime, runs the compiler in-process (a runaway template
  can no longer simply be killed), and tracks an unofficial API. Rejected for the default; the compiler seam
  (`ITypstCompiler`) leaves room for it.
- **Runtime download of the binary on first use.** Simplest for the developer, but makes production depend on GitHub
  availability and egress, and hides the binary's provenance. Rejected: the build is the place to fetch and verify.
- **Template directory as the project root.** The shape two applications had; it requires a writable template tree and
  mixes per-request data with deployed files. Rejected in favor of a package.
- **Passing data through `--input`.** Avoids writing files, but command-line length is limited (32 KB on Windows, 128 KB
  per argument on Linux), so it breaks on larger documents and cannot carry images. Inputs remain for short switches.
- **An in-process engine.** Prototyped and measured in [ADR-0094](0094-in-process-typst-engine.md): an order of
  magnitude faster for small documents, slower for large documents rendered in parallel, and without a hard timeout.
  It stays an opt-in second implementation of `ITypstCompiler`, not the default.
- **Returning `Result<TypstPdf>`.** Would make every caller handle infrastructure faults as domain outcomes and would
  differ from `IBlobStore`. Rejected; the document service maps exceptions once.

## Amendment (2026-11-27): unpacking without xz, required packages, and conveniences from the first adoption

The first application to adopt the package found one broken deployment recipe and five small gaps.

**The `.tar.xz` archives need the `xz` program under GNU tar.** The targets unpack the Linux and macOS releases with
`tar -xJf`. bsdtar (macOS, Windows) decompresses xz itself; GNU tar starts the external `xz`, which the
`mcr.microsoft.com/dotnet/sdk` images (Ubuntu 24.04) do not install, so the documented Dockerfile failed after the
download with `xz: Cannot exec`. Decision: the targets check for an unpacker before downloading (on a non-Windows
build host: `xz` on `PATH`, or a `tar` that reports itself as bsdtar) and stop with an error that names the package
to install and the `ElarionTypstExecutable` alternative; the documented Dockerfile installs `xz-utils`. Rejected:

- *Decompressing in MSBuild* (an inline or compiled task with a managed LZMA2 decoder). The BCL has no xz decoder, so
  this means carrying several hundred lines of decompressor in the build targets, or shipping a task assembly and
  its loading rules in a package that has none today, to save one line in a Dockerfile.
- *Re-hosting the binary as `.tar.gz` or `.zip`.* Moves provenance from Typst's own GitHub release to an artifact the
  framework would have to publish and keep in sync with every Typst version, which the checksum table exists to avoid.
- *Installing `xz` from the targets.* A build must not run a package manager.

**The startup check can require the application's own package.** `TypstOptions.RequiredPackages`
(`@local/acme-documents:1.0.0`) lists packages the startup check requires in `PackagePath`
(`<namespace>/<name>/<version>/typst.toml`), and `TypstToolchain.HasPackage` runs the same test at render time. Without
it, an unset `PackagePath` silently resolved to none when `typst/packages` was missing, the host started, and every
document failed with a compilation error. A compilation whose diagnostics say `package not found` now throws
`TypstPackageNotFoundException` (telemetry outcome `package_not_found`) instead of `TypstCompilationException`, so a
deployment fault is no longer reported as a template defect. The detection matches Typst's wording, checked against
0.15; if a later Typst rewords it, the failure falls back to `TypstCompilationException`, not to success. Rejected:
probing the generated package import before every compilation, because Typst also resolves packages from its data and
cache directories and would download a `@preview` package the probe called missing.

**Smaller additions.** `TypstAsset.IsValidName` and `TypstAsset.TryCreate` make the asset-name rule public, so an
application rejects a user-supplied name with its own error instead of duplicating the rule. `TypstAsset.FromJson`
serializes a second model, such as a letterhead every document receives, into an asset the template reads with
`json(assets.at("letterhead.json"))`; a second `Data` slot was rejected because one model per request plus assets
already covers it. `TypstCompiler` is public with `TypstCompiler.Create(options, loggerFactory)`, so tests and tools
build the compiler `AddElarionTypst` registers without a service collection; its constructor stays internal.
"Code defaults, then configuration" is documented as two `AddElarionTypst` calls rather than a `defaults:` delegate,
because the calls already compose onto one options instance in order.

## Amendment (2026-11-27): the compiler is not disposable

`TypstCompiler` implemented `IDisposable` to dispose its concurrency limiter, a `SemaphoreSlim` whose wait handle the
compiler never creates; disposing it released nothing. A test kit that built one compiler per test with
`TypstCompiler.Create` was left holding an undisposed disposable for no reason, and analyzers flagged it. Decision:
`TypstCompiler` no longer implements `IDisposable`. Everything that does hold a resource is released per compilation:
the process and the workspace before `CompilePdfAsync` returns, the output file when the caller disposes the
`TypstPdf`. Rejected: keeping a no-op `Dispose` for source compatibility, because it keeps telling callers there is
something to release. `TypstCompiler.Create` runs no startup check, so `RequiredPackages` is not checked there; a test
that wants the missing-package answer without the Typst binary asks `new TypstToolchain(options).HasPackage(...)`,
which only looks for the package's `typst.toml`. The PDF documents page says so.
